using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>One headline. <see cref="Link"/> stays on this computer: the phone opens an item by its id.</summary>
public sealed record FeedItem(string Id, string Title, string Source, long Published, [property: JsonIgnore] string? Link)
{
    public bool Openable => Link is not null;
}

public sealed record FeedSnapshot(IReadOnlyList<FeedItem> Items, long Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Headlines from the RSS and Atom feeds in <see cref="AgentConfig.Feeds"/>, for the Feeds widget. Each address is
/// fetched at most every 15 minutes, and only while an enabled page shows a Feeds widget; /api/refresh does not
/// bypass that. The addresses stay on this computer (private feeds carry keys in them); the phone gets titles, the
/// feed's name and times, and opens an article on this computer by its id.
/// </summary>
public sealed class FeedMonitor(ConfigStore config, ILogger<FeedMonitor> log) : BackgroundService
{
    internal const string WidgetType = "feeds";
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int MaxPerFeed = 50, MaxItems = 200;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FeedState> _feeds = [];
    private volatile FeedSnapshot _snap = new([], 0, []);

    /// <summary>Test seam: the HTTP handler behind the fetches (the real one refuses loopback and, on redirects, private addresses).</summary>
    internal HttpMessageHandler? Handler { get; init; }
    /// <summary>Test seam: opens an article on this computer.</summary>
    internal Action<Uri> Opener { get; init; } = OpenInBrowser;
    /// <summary>Test seam: the clock, in Unix milliseconds.</summary>
    internal Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private HttpClient? _http;
    private HttpClient Http => _http ??= new HttpClient(Handler ?? FeedHttp.CreateHandler(), disposeHandler: Handler is null)
        { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class FeedState
    {
        public List<FeedItem> Items = [];   // with an empty Source: the name is applied when the snapshot is built
        public long NextAt;
        public string? Error;
        public string? ETag;
        public string? LastModified;
    }

    public FeedSnapshot Snapshot() => _snap;

    public bool OpenAllowed() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("feeds: {Msg}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Fetches every feed that is due. Nothing is fetched while no enabled page shows a Feeds widget.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var sources = Sources();
        lock (_gate)
            foreach (var gone in _feeds.Keys.Except(sources.Select(s => s.Url)).ToList()) _feeds.Remove(gone);
        if (sources.Count == 0 || !OpenAllowed()) { Publish(sources); return; }

        foreach (var src in sources)
        {
            FeedState st;
            lock (_gate)
            {
                if (!_feeds.TryGetValue(src.Url, out st!)) _feeds[src.Url] = st = new FeedState();
                if (Now() < st.NextAt) continue;
                st.NextAt = Now() + (long)Interval.TotalMilliseconds; // failed fetches wait just as long
            }
            await FetchAsync(src, st, ct);
        }
        Publish(sources);
    }

    private List<FeedSource> Sources() => config.Get().Feeds
        .Where(f => !string.IsNullOrWhiteSpace(f.Url))
        .GroupBy(f => f.Url, StringComparer.Ordinal).Select(g => g.First()).ToList();

    private async Task FetchAsync(FeedSource src, FeedState st, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await FeedHttp.GetAsync(Http, src.Url, st.ETag, st.LastModified, timeout.Token);
            if (r.NotModified) { lock (_gate) st.Error = null; return; }
            var items = FeedParser.Parse(r.Body!, r.FinalUri!, src.Url, MaxPerFeed);
            lock (_gate) { st.Items = items; st.Error = null; st.ETag = r.ETag; st.LastModified = r.LastModified; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = ex is OperationCanceledException ? "no answer in 20 s" : ex is XmlException ? "not an RSS or Atom feed" : ex.Message;
            lock (_gate) st.Error = msg; // the last good headlines stay
            log.LogInformation("feed {Name}: {Msg}", NameOf(src), msg);
        }
    }

    private static string NameOf(FeedSource s) =>
        !string.IsNullOrWhiteSpace(s.Name) ? s.Name.Trim() : Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Host : "Feed";

    private void Publish(List<FeedSource> sources)
    {
        var items = new List<FeedItem>();
        var errors = new List<string>();
        lock (_gate)
            foreach (var src in sources)
            {
                if (!_feeds.TryGetValue(src.Url, out var st)) continue;
                var name = NameOf(src);
                items.AddRange(st.Items.Select(i => i with { Source = name }));
                if (st.Error is { } e) errors.Add($"{name}: {e}");
            }
        // newest first; undated items keep their feed order after the dated ones
        var ordered = items.Select((it, i) => (it, i)).OrderByDescending(x => x.it.Published).ThenBy(x => x.i)
            .Select(x => x.it).Take(MaxItems).ToList();
        _snap = new(ordered, Now(), errors);
    }

    /// <summary>Opens a headline from the current list on this computer; returns an error message, or null when it worked.</summary>
    public string? Open(string? id)
    {
        if (!OpenAllowed()) return "Add a News feeds widget to a page to open articles from the phone";
        if (string.IsNullOrEmpty(id) || _snap.Items.FirstOrDefault(i => i.Id == id) is not { } item) return "That headline is no longer in the list";
        if (FeedParser.SafeLink(item.Link) is not { } uri) return "This headline has no link";
        try { Opener(uri); }
        catch (Exception ex) { log.LogWarning("feed: could not open {Host}: {Msg}", uri.Host, ex.Message); return "Could not open it on the computer"; }
        log.LogInformation("feed article opened from the phone: {Host}", uri.Host);
        return null;
    }

    private static void OpenInBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    public override void Dispose()
    {
        _http?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Outbound requests for feeds. Never to this computer (loopback), link-local, multicast or unspecified addresses.
/// Private and Tailscale addresses are allowed only for the host the user typed in Settings (a self-hosted reader
/// at home); a redirect to another host must go to a public address. Redirects are followed by hand, at most 5.
/// </summary>
internal static class FeedHttp
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly HttpRequestOptionsKey<bool> AllowPrivate = new("PhoneBox.AllowPrivate");

    internal sealed record Response(byte[]? Body, Uri? FinalUri, bool NotModified, string? ETag, string? LastModified);

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        // pooled connections are keyed by host, and whether private addresses are allowed depends only on the host
        ConnectCallback = async (ctx, token) =>
        {
            var allowPrivate = ctx.InitialRequestMessage.Options.TryGetValue(AllowPrivate, out var p) && p;
            var addresses = IPAddress.TryParse(ctx.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
            var ip = addresses.FirstOrDefault(a => Allowed(a, allowPrivate))
                ?? throw new HttpRequestException(allowPrivate
                    ? "refused: the address is this computer or not routable"
                    : "refused: a redirect pointed to a local or private address");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Public addresses always; private, CGNAT/Tailscale and unique-local ones only when <paramref name="allowPrivate"/>.</summary>
    internal static bool Allowed(IPAddress ip, bool allowPrivate)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (WidgetFeedProxy.IsPublicAddress(ip)) return true;
        return allowPrivate && IsPrivate(ip);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>An http(s) address without user info, or null.</summary>
    internal static Uri? ParseTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2048) return null;
        raw = raw.Trim();
        if (raw.StartsWith("feed://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw[7..];
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(u.UserInfo) ? u : null;
    }

    internal static async Task<Response> GetAsync(HttpClient http, string configured, string? etag, string? lastModified, CancellationToken ct)
    {
        var first = ParseTarget(configured) ?? throw new HttpRequestException("the address must start with http:// or https://");
        var uri = first;
        for (var hop = 0; ; hop++)
        {
            var sameHost = string.Equals(uri.Host, first.Host, StringComparison.OrdinalIgnoreCase);
            // a redirect to a literal address can be refused before any connection
            if (!sameHost && IPAddress.TryParse(uri.Host, out var lit) && !Allowed(lit, allowPrivate: false))
                throw new HttpRequestException("refused: a redirect pointed to a local or private address");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Options.Set(AllowPrivate, sameHost);
            req.Headers.UserAgent.ParseAdd($"PhoneBox/{AppPaths.Version}");
            req.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.1");
            if (hop == 0 && etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (hop == 0 && lastModified is not null) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= MaxRedirects) throw new HttpRequestException("too many redirects");
                var loc = res.Headers.Location ?? throw new HttpRequestException($"HTTP {code} without a Location");
                uri = ParseTarget(new Uri(uri, loc).AbsoluteUri) ?? throw new HttpRequestException("redirected to an address that is not http(s)");
                continue;
            }
            if (res.StatusCode == HttpStatusCode.NotModified) return new(null, uri, true, etag, lastModified);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {code}");
            if (res.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("the feed is larger than 4 MB");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var buf = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buf.Length + n > MaxBytes) throw new HttpRequestException("the feed is larger than 4 MB");
                buf.Write(chunk, 0, n);
            }
            return new(buf.ToArray(), uri, false, res.Headers.ETag?.ToString(), res.Content.Headers.LastModified?.ToString("R"));
        }
    }
}

/// <summary>RSS 2.0, RSS 1.0 (RDF) and Atom. No DTDs, no external entities; titles become plain text.</summary>
internal static partial class FeedParser
{
    private const int MaxTitle = 300;

    internal static List<FeedItem> Parse(byte[] xml, Uri baseUri, string feedKey, int max)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // old RSS 0.91 feeds carry a DOCTYPE; it is skipped, never loaded
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = FeedHttp.MaxBytes * 2L,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        // from the bytes, so the encoding in the XML declaration wins over a wrong HTTP charset
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new XmlException("empty document");
        if (root.Name.LocalName is not ("rss" or "RDF" or "feed")) throw new XmlException("not a feed");

        var atom = root.Name.LocalName == "feed";
        var entries = root.Descendants().Where(e => e.Name.LocalName == (atom ? "entry" : "item"));
        var list = new List<FeedItem>();
        foreach (var e in entries)
        {
            var title = atom ? AtomText(Child(e, "title")) : Clean(Child(e, "title")?.Value);
            var link = atom ? AtomLink(e, baseUri) : RssLink(e, baseUri);
            var key = (atom ? Child(e, "id")?.Value : Child(e, "guid")?.Value)?.Trim();
            if (string.IsNullOrEmpty(key)) key = link ?? title;
            if (string.IsNullOrEmpty(title)) title = link is not null && Uri.TryCreate(link, UriKind.Absolute, out var lu) ? lu.Host : "";
            if (string.IsNullOrEmpty(title)) continue;
            var date = atom
                ? Child(e, "published")?.Value ?? Child(e, "updated")?.Value
                : Child(e, "pubDate")?.Value ?? Child(e, "date")?.Value; // dc:date in RSS 1.0
            list.Add(new FeedItem(IdOf(feedKey, key!), title, "", ParseDate(date), link));
            if (list.Count >= max) break;
        }
        return list;
    }

    internal static string IdOf(string feedKey, string itemKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(feedKey + "\n" + itemKey)))[..16];

    private static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    private static string AtomText(XElement? t)
    {
        if (t is null) return "";
        var type = (string?)t.Attribute("type");
        // type="html" holds escaped markup; xhtml holds elements whose text is the title
        return type == "html" ? Clean(t.Value) : Clean(t.Value, decodeEntities: false);
    }

    private static string? AtomLink(XElement e, Uri baseUri)
    {
        var links = e.Elements().Where(c => c.Name.LocalName == "link").ToList();
        var href = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value
            ?? links.FirstOrDefault()?.Attribute("href")?.Value;
        return Absolute(href, baseUri);
    }

    private static string? RssLink(XElement e, Uri baseUri)
    {
        if (Absolute(Child(e, "link")?.Value, baseUri) is { } l) return l;
        var guid = Child(e, "guid");
        return guid is not null && !string.Equals((string?)guid.Attribute("isPermaLink"), "false", StringComparison.OrdinalIgnoreCase)
            ? Absolute(guid.Value, baseUri) : null;
    }

    private static string? Absolute(string? href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(baseUri, href.Trim(), out var u) ? SafeLink(u.AbsoluteUri)?.AbsoluteUri : null;
    }

    /// <summary>Only http and https links are ever opened (no file:, javascript:, custom app schemes).</summary>
    internal static Uri? SafeLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.AbsoluteUri.Length <= 4096
            ? u : null;

    /// <summary>Plain text: HTML tags and entities resolved, control and direction-override characters dropped, capped.</summary>
    internal static string Clean(string? s, bool decodeEntities = true)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (decodeEntities) s = WebUtility.HtmlDecode(Tags().Replace(s, " "));
        var sb = new StringBuilder(Math.Min(s.Length, MaxTitle + 1));
        var space = false;
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r) || Rune.IsControl(r)) { space = sb.Length > 0; continue; }
            // direction overrides, isolates and marks would let a title rearrange the text around it
            if (r.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 or 0x200E or 0x200F or 0x061C or 0xFEFF) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(r.ToString());
            if (sb.Length >= MaxTitle) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^(?:[A-Za-z]+,?\s*)?(\d{1,2})\s+([A-Za-z]{3})[A-Za-z]*\.?\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(.*)$")]
    private static partial Regex Rfc822();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5, ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
    };

    /// <summary>RFC 822 (RSS) and ISO 8601 (Atom, dc:date) dates in Unix ms; 0 when unreadable.</summary>
    internal static long ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        var m = Rfc822().Match(s);
        if (m.Success)
        {
            var mon = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant());
            if (mon < 0) return 0;
            var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (m.Groups[3].Value.Length == 2) year += year < 50 ? 2000 : 1900;
            var zone = m.Groups[7].Value.Trim();
            var offset = TimeSpan.Zero;
            if (zone.Length == 5 && zone[0] is '+' or '-' && int.TryParse(zone.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
                offset = TimeSpan.FromMinutes((hhmm / 100 * 60 + hhmm % 100) * (zone[0] == '-' ? -1 : 1));
            else if (Zones.TryGetValue(zone, out var hours)) offset = TimeSpan.FromHours(hours);
            try
            {
                var sec = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
                var d = new DateTimeOffset(year, mon + 1, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Math.Min(sec, 59), offset);
                return d.ToUnixTimeMilliseconds();
            }
            catch (ArgumentOutOfRangeException) { return 0; }
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var iso)
            ? iso.ToUnixTimeMilliseconds() : 0;
    }
}
