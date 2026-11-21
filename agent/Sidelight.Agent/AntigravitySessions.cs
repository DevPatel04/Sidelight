using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sidelight.Agent;

/// <summary>
/// Watches recent Antigravity agent conversations through the IDE's own language server on this computer (the same
/// loopback RPC the quota reader uses, found the same way). Only the list call is made, and only its metadata is
/// kept: id, run status, times and the workspace folder's name. The reply also carries summaries and the agent's last
/// message; those are never kept, shown or sent. An IDE that is not running contributes nothing.
/// </summary>
internal static class AntigravitySessions
{
    private const int Hours = 3;
    private const int MaxSessions = 20;
    private const int MaxReplyBytes = 8 << 20;
    // The sessions loop runs every 2 seconds; the list carries every conversation, so it is asked for less often.
    private const long ReadEveryMs = 5_000;
    // Finding the server reads process command lines and listening ports: only while no known server answers.
    private const long DiscoverEveryMs = 30_000;

    private static readonly object Lock = new();
    private static HttpClient? _http;
    private static QuotaMonitor.AntigravityEndpoint? _endpoint;
    private static long _nextDiscovery;
    private static long _nextRead;
    private static List<AgentSession> _last = [];

    /// <summary>For tests: a fake handler instead of the network.</summary>
    internal static HttpMessageHandler? HttpHandler { get; set; }

    private static HttpClient Http
    {
        get
        {
            lock (Lock)
            {
                // the server's HTTPS port has a self-signed certificate, accepted for loopback addresses only
                return _http ??= new HttpClient(HttpHandler ?? QuotaMonitor.NewLoopbackTlsHandler()) { Timeout = TimeSpan.FromSeconds(3) };
            }
        }
    }

    internal static async Task<List<AgentSession>> ReadAsync(ILogger? log = null, CancellationToken ct = default)
    {
        var tick = Environment.TickCount64;
        if (tick < _nextRead) return _last;
        _nextRead = tick + ReadEveryMs;

        var candidates = _endpoint is { } known ? new[] { known } : [];
        if (candidates.Length == 0)
        {
            if (tick < _nextDiscovery) return _last = [];
            _nextDiscovery = tick + DiscoverEveryMs;
            candidates = QuotaMonitor.FindAntigravityEndpoints().Distinct().Take(8).ToArray();
        }
        foreach (var ep in candidates)
        {
            try
            {
                if (await ReadEndpointAsync(Http, ep, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct) is { } list)
                {
                    _endpoint = ep;
                    return _last = list;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                log?.LogDebug("antigravity sessions ({Endpoint}): {Msg}", ep, ex.Message);
            }
        }
        // the IDE was closed or restarted on new ports: look for it again on a later round
        _endpoint = null;
        return _last = [];
    }

    /// <summary>The recent conversations of one server, or null when it is not an Antigravity server that answers.</summary>
    internal static async Task<List<AgentSession>?> ReadEndpointAsync(HttpClient http, QuotaMonitor.AntigravityEndpoint ep,
        long nowMs, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{ep}/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories");
        req.Headers.Add("X-Codeium-Csrf-Token", ep.CsrfToken);
        req.Headers.Add("Connect-Protocol-Version", "1");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxReplyBytes) return null;
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxReplyBytes) return null;
            buffer.Write(chunk, 0, n);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return Parse(doc.RootElement, nowMs);
    }

    /// <summary>Sessions from a GetAllCascadeTrajectories reply: running ones, and the rest touched in the last hours.</summary>
    internal static List<AgentSession>? Parse(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("trajectorySummaries", out var all)) return []; // a server with no conversations yet
        if (all.ValueKind != JsonValueKind.Object) return null;
        var cutoff = nowMs - Hours * 3600_000L;
        var list = new List<AgentSession>();
        foreach (var entry in all.EnumerateObject())
        {
            var e = entry.Value;
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = entry.Name;
            var updated = Time(e, "lastModifiedTime");
            var busy = Str(e, "status") == "CASCADE_RUN_STATUS_RUNNING";
            if (id.Length == 0 || updated is null || !busy && updated < cutoff) continue;
            var project = Project(e);
            list.Add(new AgentSession(
                Id: "antigravity:" + id,
                Tool: "antigravity",
                // the summary is drawn from the conversation, so it is not used: folder plus a short id, like Cursor's
                Name: $"{project.ToLowerInvariant()}-{(id.Length > 4 ? id[..4] : id)}",
                Project: project,
                Status: busy ? "busy" : "idle",
                WaitingFor: null,
                Since: updated.Value,
                StartedAt: Time(e, "createdTime") ?? updated.Value,
                TurnStartedAt: busy ? Time(e, "lastUserInputTime") : null));
        }
        return list.OrderByDescending(s => s.Status == "busy").ThenByDescending(s => s.Since).Take(MaxSessions).ToList();
    }

    /// <summary>The first workspace folder's name; only the name, never the rest of the path.</summary>
    private static string Project(JsonElement e)
    {
        if (e.TryGetProperty("workspaces", out var ws) && ws.ValueKind == JsonValueKind.Array)
            foreach (var w in ws.EnumerateArray())
                if (Str(w, "workspaceFolderAbsoluteUri") is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
                {
                    var name = Path.GetFileName(u.LocalPath.TrimEnd('/', '\\'));
                    if (name.Length > 0) return name;
                }
        return "antigravity";
    }

    private static long? Time(JsonElement e, string p) =>
        Str(e, p) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() : null;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>For tests: forget the server and the last list.</summary>
    internal static void Reset()
    {
        lock (Lock) { _http?.Dispose(); _http = null; }
        _endpoint = null;
        _nextDiscovery = _nextRead = 0;
        _last = [];
    }
}
