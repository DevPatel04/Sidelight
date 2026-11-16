using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhoneBox.Agent;

internal enum SpeechHttpScope
{
    PublicCloudOnly = 0,
    LoopbackOnly = 1,
    PrivateLanOnly = 2,
}

/// <summary>SSRF-hardened HTTP helpers for external cloud STT APIs and opt-in loopback or private LAN local STT servers.</summary>
internal static class SafeSpeechHttp
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const long MaxAudioBytes = 25L * 1024 * 1024;
    private const int MaxRedirects = 3;

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo || ip.IsIPv6UniqueLocal)
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                                     // 0.0.0.0/8
            if (b[0] == 10) return false;                                    // 10.0.0.0/8 (RFC1918)
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;       // 100.64.0.0/10 (CGNAT)
            if (b[0] == 127) return false;                                   // 127.0.0.0/8 (loopback)
            if (b[0] == 169 && b[1] == 254) return false;                    // 169.254.0.0/16 (link-local / metadata)
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;        // 172.16.0.0/12 (RFC1918)
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;         // 192.0.0.0/24 (IETF protocol)
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return false;         // 192.0.2.0/24 (TEST-NET-1)
            if (b[0] == 192 && b[1] == 168) return false;                    // 192.168.0.0/16 (RFC1918)
            if (b[0] == 198 && b[1] is 18 or 19) return false;               // 198.18.0.0/15 (benchmarking)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;      // 198.51.100.0/24 (TEST-NET-2)
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;       // 203.0.113.0/24 (TEST-NET-3)
            if (b[0] >= 224) return false;                                   // 224.0.0.0/4 multicast + reserved
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false;                         // fc00::/7 (ULA)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 (docs)
            if (b[0] == 0x20 && b[1] == 0x02) return false;                  // 2002::/16 (6to4)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
            {
                if (b[4] == 0x00 && b[5] == 0x01) return false;              // 64:ff9b:1::/48 (NAT64 local-use)
                if (b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
                    return false;                                            // 64:ff9b::/96 (NAT64 well-known)
            }
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0
                && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
                return false;                                                // ::/96 (IPv4-compatible IPv6)
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0
                && b[6] == 0 && b[7] == 0 && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0)
                return false;                                                // ::ffff:0:0:0/96 (SIIT)
            return true;
        }

        return false;
    }

    /// <summary>Returns true strictly for RFC1918 IPv4 and ULA (fc00::/7) IPv6 LAN addresses (never loopback, metadata/link-local, CGNAT, or public IPs).</summary>
    public static bool IsPrivateLanAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6None)
            || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo)
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                                     // 10.0.0.0/8
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;         // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                     // 192.168.0.0/16
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6UniqueLocal || (b[0] & 0xfe) == 0xfc;            // fc00::/7 ULA
        }

        return false;
    }

    public static bool IsExplicitLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return IPAddress.IsLoopback(ip);
        }
        return false;
    }

    /// <summary>Validates a Local HTTP URL syntactically: loopback (127.0.0.1 / ::1 / localhost) or, when allowLan is enabled, private RFC1918/ULA HTTPS.</summary>
    public static bool TryValidateLocalEndpoint(
        string? rawUrl,
        bool allowLan,
        IReadOnlyCollection<int> blockedPorts,
        out Uri? uri,
        out SpeechHttpScope scope,
        out string? error)
    {
        uri = null;
        scope = SpeechHttpScope.LoopbackOnly;
        error = null;

        if (!Uri.TryCreate((rawUrl ?? "").Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "Local server URL must be an http:// or https:// URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Embedded credentials in URLs are not allowed.";
            return false;
        }

        if (blockedPorts.Contains(parsed.Port))
        {
            error = $"Local server port {parsed.Port} is reserved by PhoneBox.";
            return false;
        }

        if (IsExplicitLoopbackHost(parsed.Host))
        {
            scope = SpeechHttpScope.LoopbackOnly;
            uri = parsed;
            return true;
        }

        if (!allowLan)
        {
            error = "Only loopback hosts (127.0.0.1, ::1, localhost) are allowed unless 'Allow private LAN HTTPS servers' is enabled in Settings.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Private LAN endpoints must use HTTPS (plain HTTP is allowed only for 127.0.0.1 / ::1 / localhost).";
            return false;
        }

        if (IPAddress.TryParse(parsed.Host, out var literalIp))
        {
            if (!IsPrivateLanAddress(literalIp))
            {
                error = "Only loopback or private RFC1918/ULA LAN IP addresses (e.g. 192.168.x.x, 10.x.x.x) are allowed; public internet and link-local addresses are blocked.";
                return false;
            }
        }

        scope = SpeechHttpScope.PrivateLanOnly;
        uri = parsed;
        return true;
    }

    /// <summary>Validates and resolves a Local HTTP endpoint up front so DNS or non-private LAN hostnames (such as unresolvable .local names or public IPs) report clearly.</summary>
    public static async Task<(bool Ok, Uri? Uri, SpeechHttpScope Scope, string? Error)> ValidateAndResolveLocalEndpointAsync(
        string? rawUrl,
        bool allowLan,
        IReadOnlyCollection<int> blockedPorts,
        CancellationToken ct,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        if (!TryValidateLocalEndpoint(rawUrl, allowLan, blockedPorts, out var uri, out var scope, out var err) || uri is null)
            return (false, null, SpeechHttpScope.LoopbackOnly, err);

        if (scope == SpeechHttpScope.LoopbackOnly || IPAddress.TryParse(uri.Host, out _))
            return (true, uri, scope, null);

        IPAddress[] resolved;
        try
        {
            resolved = dnsResolver is not null
                ? await dnsResolver(uri.Host, ct)
                : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, scope, $"Could not resolve local hostname '{uri.Host}' ({ex.Message}). Use its private LAN IP address instead.");
        }

        if (resolved.Length == 0)
            return (false, null, scope, $"Local hostname '{uri.Host}' resolved to no addresses. Use its private LAN IP address instead.");

        if (!resolved.All(IsPrivateLanAddress))
        {
            return (false, null, scope, $"Host '{uri.Host}' resolved outside private RFC1918/ULA LAN ranges; public internet, loopback, and link-local/metadata targets are blocked.");
        }

        return (true, uri, scope, null);
    }

    internal static SocketsHttpHandler CreateHandler(SpeechHttpScope scope, IReadOnlyCollection<int> blockedPorts) =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = (ctx, ct) => ConnectValidatedAsync(ctx, scope, blockedPorts, ct),
        };

    public static HttpClient CreateClient(bool allowLoopback, IReadOnlyCollection<int> blockedPorts, TimeSpan timeout) =>
        CreateClient(allowLoopback ? SpeechHttpScope.LoopbackOnly : SpeechHttpScope.PublicCloudOnly, blockedPorts, timeout);

    public static HttpClient CreateClient(SpeechHttpScope scope, IReadOnlyCollection<int> blockedPorts, TimeSpan timeout) =>
        new(CreateHandler(scope, blockedPorts), disposeHandler: true)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext ctx,
        SpeechHttpScope scope,
        IReadOnlyCollection<int> blockedPorts,
        CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        if (blockedPorts.Contains(port))
            throw new HttpRequestException($"Connection to reserved PhoneBox port {port} is blocked.");

        if (scope == SpeechHttpScope.LoopbackOnly)
        {
            if (!IsExplicitLoopbackHost(host))
                throw new HttpRequestException("Only explicit loopback targets (127.0.0.1, ::1, localhost) are permitted.");
            IPAddress[] targets = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : [IPAddress.Loopback, IPAddress.IPv6Loopback];
            return await ConnectFirstAsync(targets, port, scope, ct);
        }

        IPAddress[] resolved = IPAddress.TryParse(host, out var directIp)
            ? [directIp]
            : await Dns.GetHostAddressesAsync(host, ct);

        return await ConnectFirstAsync(resolved, port, scope, ct);
    }

    private static async Task<Stream> ConnectFirstAsync(
        IPAddress[] addresses,
        int port,
        SpeechHttpScope scope,
        CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var raw in addresses)
        {
            var ip = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            var permitted = scope switch
            {
                SpeechHttpScope.PublicCloudOnly => IsPublicAddress(ip),
                SpeechHttpScope.LoopbackOnly => IPAddress.IsLoopback(ip),
                SpeechHttpScope.PrivateLanOnly => IsPrivateLanAddress(ip),
                _ => false,
            };
            if (!permitted) continue;

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(ip, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            $"Target host resolved to no permitted address ({addresses.Length} candidate(s)).",
            lastError);
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static bool CanAttachCredentials(Uri uri, SpeechHttpScope scope, IReadOnlyCollection<string>? allowedCredentialHosts)
    {
        if (allowedCredentialHosts is { Count: > 0 })
        {
            return uri.Scheme == Uri.UriSchemeHttps
                && allowedCredentialHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (scope == SpeechHttpScope.LoopbackOnly && uri.Scheme == Uri.UriSchemeHttp && IsExplicitLoopbackHost(uri.Host));
    }

    public static Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        HttpClient client,
        Func<Uri, bool, HttpRequestMessage> requestFactory,
        Uri initialUri,
        bool allowLoopback,
        IReadOnlyCollection<int> blockedPorts,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct) =>
        SendWithSafeRedirectsAsync(
            client,
            requestFactory,
            initialUri,
            allowLoopback ? SpeechHttpScope.LoopbackOnly : SpeechHttpScope.PublicCloudOnly,
            blockedPorts,
            allowedCredentialHosts,
            ct);

    /// <summary>Executes a request with manual redirect validation, an overall timeout budget, strict credential host scoping, and write protection.</summary>
    public static async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        HttpClient client,
        Func<Uri, bool, HttpRequestMessage> requestFactory,
        Uri initialUri,
        SpeechHttpScope scope,
        IReadOnlyCollection<int> blockedPorts,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct)
    {
        var currentUri = initialUri;
        var includeAuth = true;
        var forceGet = false;

        using var budgetCts = client.Timeout > TimeSpan.Zero && client.Timeout != Timeout.InfiniteTimeSpan
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (budgetCts is not null)
            budgetCts.CancelAfter(client.Timeout);
        var effectiveCt = budgetCts?.Token ?? ct;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var sendAuth = includeAuth && CanAttachCredentials(currentUri, scope, allowedCredentialHosts);
            using var req = requestFactory(currentUri, sendAuth);
            if (forceGet && req.Method != HttpMethod.Get && req.Method != HttpMethod.Head)
            {
                req.Method = HttpMethod.Get;
                req.Content?.Dispose();
                req.Content = null;
            }

            var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, effectiveCt);
            var code = (int)res.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || res.Headers.Location is null)
                return res;

            var nextUri = res.Headers.Location.IsAbsoluteUri
                ? res.Headers.Location
                : new Uri(currentUri, res.Headers.Location);
            res.Dispose();

            if (!IsPermittedRedirectTarget(currentUri, nextUri, scope, blockedPorts, out var reason))
                throw new HttpRequestException(reason);

            if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head && !SameOrigin(currentUri, nextUri))
                throw new HttpRequestException("Blocked cross-origin redirect for non-GET request.");

            if (code is 301 or 302 or 303 && req.Method != HttpMethod.Get && req.Method != HttpMethod.Head)
                forceGet = true;

            if (!SameOrigin(currentUri, nextUri))
                includeAuth = false;

            currentUri = nextUri;
        }

        throw new HttpRequestException($"Too many HTTP redirects (>{MaxRedirects}).");
    }

    public static bool IsPermittedRedirectTarget(
        Uri from,
        Uri to,
        SpeechHttpScope scope,
        IReadOnlyCollection<int> blockedPorts,
        out string reason)
    {
        reason = "";
        if (!string.IsNullOrEmpty(to.UserInfo))
        {
            reason = "Redirect target with credentials is blocked.";
            return false;
        }

        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme != Uri.UriSchemeHttps)
        {
            reason = "Blocked unsafe HTTPS -> HTTP redirect.";
            return false;
        }

        if (blockedPorts.Contains(to.Port))
        {
            reason = "Blocked redirect to reserved PhoneBox port.";
            return false;
        }

        switch (scope)
        {
            case SpeechHttpScope.PublicCloudOnly:
                if (to.Scheme != Uri.UriSchemeHttps)
                {
                    reason = "Blocked redirect to non-HTTPS target.";
                    return false;
                }
                if (IsExplicitLoopbackHost(to.Host) || (IPAddress.TryParse(to.Host, out var pubIp) && !IsPublicAddress(pubIp)))
                {
                    reason = "Blocked redirect to non-public target.";
                    return false;
                }
                return true;

            case SpeechHttpScope.LoopbackOnly:
                if ((to.Scheme != Uri.UriSchemeHttp && to.Scheme != Uri.UriSchemeHttps) || !IsExplicitLoopbackHost(to.Host))
                {
                    reason = "Blocked redirect to non-loopback target.";
                    return false;
                }
                return true;

            case SpeechHttpScope.PrivateLanOnly:
                if (to.Scheme != Uri.UriSchemeHttps)
                {
                    reason = "Blocked redirect to non-HTTPS private LAN target.";
                    return false;
                }
                if (IsExplicitLoopbackHost(to.Host))
                {
                    reason = "Blocked redirect to loopback host.";
                    return false;
                }
                if (IPAddress.TryParse(to.Host, out var lanIp) && !IsPrivateLanAddress(lanIp))
                {
                    reason = "Blocked redirect outside private RFC1918/ULA LAN range.";
                    return false;
                }
                return true;

            default:
                reason = "Blocked redirect target.";
                return false;
        }
    }

    public static async Task<byte[]> ReadBoundedAudioBytesAsync(string wavPath, CancellationToken ct)
    {
        var info = new FileInfo(wavPath);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidOperationException("Captured audio file is missing or empty.");
        if (info.Length > MaxAudioBytes)
            throw new InvalidOperationException($"Captured audio exceeds maximum upload size ({MaxAudioBytes / (1024 * 1024)} MB).");
        return await File.ReadAllBytesAsync(wavPath, ct);
    }

    public static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        var limit = MaxResponseBytes + 1;
        while (ms.Length < limit)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, limit - (int)ms.Length)), ct);
            if (read == 0) break;
            ms.Write(buf, 0, read);
        }

        if (ms.Length > MaxResponseBytes)
            throw new InvalidOperationException($"Speech provider response exceeded maximum size ({MaxResponseBytes} bytes).");

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
