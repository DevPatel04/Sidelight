using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record DiscordMember(string Id, string Name, bool Muted, bool Deafened, string? Avatar);
public sealed record DiscordChannel(string Id, string Name, string? Guild, string? GuildId, string? GuildIcon);
public sealed record DiscordNotification(long Ts, string Title, string Body);
public sealed record DiscordSnapshot(bool Connected, string? Error, string? User, bool Muted, bool Deafened,
    string? ChannelId, string? ChannelName, string? ChannelKind, IReadOnlyList<DiscordMember> Members,
    IReadOnlyList<DiscordChannel> Channels, IReadOnlyList<DiscordNotification> Notifications);

/// <summary>
/// Discord through the desktop app's local IPC (the same one rich presence uses: a named pipe on Windows, a socket on
/// Linux / macOS). Needs a Discord developer application (client id + secret) and a one-time "Authorize" click in
/// Discord. Reads the voice channel you are in (also DM / group calls), mute and deafen, the voice channels you can
/// join and the notifications Discord shows, and runs the widget's buttons. Nothing leaves the computer except the
/// OAuth token exchange with discord.com.
/// </summary>
public sealed partial class DiscordMonitor(ConfigStore config, ILogger<DiscordMonitor> log) : BackgroundService
{
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write", "rpc.notifications.read"];
    private const string Redirect = "http://127.0.0.1";

    private volatile DiscordSnapshot _snap = Empty(null);
    private readonly object _lock = new();
    private readonly List<DiscordNotification> _notes = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private Stream? _io;

    private static DiscordSnapshot Empty(string? error) => new(false, error, null, false, false, null, null, null, [], [], []);

    public DiscordSnapshot Snapshot() { lock (_lock) return _snap with { Notifications = [.. _notes] }; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            var cfg = config.Get().Discord;
            if (!cfg.Enabled) { _snap = Empty(null); await Task.Delay(2000, ct); continue; }
            try { await RunConnection(cfg, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var msg = ex is IOException or TimeoutException or SocketException ? "Discord is not running" : ex.Message;
                _snap = Empty(msg);
                if (msg != lastError) { log.LogInformation("discord: {Msg}", msg); lastError = msg; }
            }
            await Task.Delay(5000, ct);
        }
    }

    private async Task RunConnection(DiscordConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientId) || string.IsNullOrWhiteSpace(cfg.ClientSecret))
            throw new Exception("Enter the application's client ID and secret (Settings › General › Discord)");
        await using var io = await OpenIpc(ct);
        // handshake
        await WriteFrame(io, 0, new JsonObject { ["v"] = 1, ["client_id"] = cfg.ClientId }, ct);
        var ready = await ReadFrame(io, ct) ?? throw new IOException("Discord closed the connection");
        if (ready["evt"]?.GetValue<string>() != "READY") throw new Exception(ready["data"]?["message"]?.GetValue<string>() ?? "Discord refused the client ID");
        _io = io;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() => ReadLoop(io, stop.Token), CancellationToken.None);
        try
        {
            _snap = Empty("Waiting for you to approve PhoneBox in Discord…");
            var user = await Authenticate(cfg, ct);
            log.LogInformation("discord: connected as {User}", user);
            await Request("SUBSCRIBE", new JsonObject(), "NOTIFICATION_CREATE", ct);
            _guildChannels.Clear();
            var channelsAt = DateTime.MinValue;
            var channelsEvery = TimeSpan.FromSeconds(60);
            while (!ct.IsCancellationRequested && !reader.IsCompleted && config.Get().Discord.Enabled)
            {
                if (DateTime.UtcNow - channelsAt > channelsEvery)
                {
                    channelsEvery = await RefreshVoiceChannels(ct) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(10);
                    channelsAt = DateTime.UtcNow;
                }
                var channels = _guildChannels.Values.SelectMany(v => v).ToList();
                var rev = Volatile.Read(ref _voiceRev);
                var vs = await Request("GET_VOICE_SETTINGS", null, null, ct);
                var sel = await Request("GET_SELECTED_VOICE_CHANNEL", null, null, ct);
                var members = sel?["voice_states"]?.AsArray().Select(v => new DiscordMember(
                    v?["user"]?["id"]?.GetValue<string>() ?? "",
                    v?["nick"]?.GetValue<string>() ?? v?["user"]?["global_name"]?.GetValue<string>() ?? v?["user"]?["username"]?.GetValue<string>() ?? "?",
                    v?["voice_state"]?["mute"]?.GetValue<bool>() == true || v?["voice_state"]?["self_mute"]?.GetValue<bool>() == true,
                    v?["voice_state"]?["deaf"]?.GetValue<bool>() == true || v?["voice_state"]?["self_deaf"]?.GetValue<bool>() == true,
                    v?["user"]?["avatar"]?.GetValue<string>())).ToList() ?? [];
                var kind = sel?["type"]?.GetValue<int>() switch { 1 => "call", 3 => "group call", 2 => "voice", 13 => "stage", _ => sel is null ? null : "voice" };
                PublishPoll(rev, vs?["mute"]?.GetValue<bool>() == true, vs?["deaf"]?.GetValue<bool>() == true,
                    new DiscordSnapshot(true, null, user, false, false,
                        sel?["id"]?.GetValue<string>(), sel is null ? null : (sel["name"]?.GetValue<string>() ?? (kind == "call" ? "Direct call" : "Call")), kind, members, channels, []));
                await Task.Delay(2000, ct);
            }
        }
        finally
        {
            _io = null;
            stop.Cancel();
            try { await reader; } catch { }
            foreach (var p in _pending.Values) p.TrySetResult(null);
            _pending.Clear();
        }
    }

    // AUTHORIZE (asks you in Discord, once) → token exchange → AUTHENTICATE. Tokens are kept in the settings folder.
    private async Task<string> Authenticate(DiscordConfig cfg, CancellationToken ct)
    {
        var tokens = LoadTokens(cfg.ClientId!);
        if (tokens is not null && tokens.Expires < DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
            tokens = await Exchange(cfg, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.Refresh }, ct);
        if (tokens is not null)
        {
            try { return await Auth(tokens.Access, ct); } catch (Exception ex) { log.LogInformation("discord: saved login no longer works ({Msg})", ex.Message); }
        }
        var granted = await Request("AUTHORIZE", new JsonObject { ["client_id"] = cfg.ClientId, ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()) }, null, ct, TimeSpan.FromMinutes(2));
        var code = granted?["code"]?.GetValue<string>() ?? throw new Exception("Discord did not give a login code");
        tokens = await Exchange(cfg, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect }, ct)
            ?? throw new Exception("Discord refused the client secret or the redirect (add http://127.0.0.1 under OAuth2 › Redirects)");
        return await Auth(tokens.Access, ct);
    }

    private async Task<string> Auth(string token, CancellationToken ct)
    {
        var r = await Request("AUTHENTICATE", new JsonObject { ["access_token"] = token }, null, ct);
        return r?["user"]?["global_name"]?.GetValue<string>() ?? r?["user"]?["username"]?.GetValue<string>() ?? "Discord";
    }

    private sealed record Tokens(string ClientId, string Access, string Refresh, long Expires);

    private static string TokenPath => Path.Combine(AppPaths.ConfigDir, "discord-token.json");

    private static Tokens? LoadTokens(string clientId)
    {
        try
        {
            var t = JsonSerializer.Deserialize<Tokens>(File.ReadAllText(TokenPath));
            return t is not null && t.ClientId == clientId ? t : null;
        }
        catch { return null; }
    }

    private async Task<Tokens?> Exchange(DiscordConfig cfg, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = cfg.ClientId!; form["client_secret"] = cfg.ClientSecret!;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var res = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode) return null;
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var t = new Tokens(cfg.ClientId!, j?["access_token"]?.GetValue<string>() ?? "", j?["refresh_token"]?.GetValue<string>() ?? "",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (j?["expires_in"]?.GetValue<long>() ?? 3600));
        if (t.Access == "") return null;
        try { Directory.CreateDirectory(AppPaths.ConfigDir); AppPaths.WriteAllTextPrivate(TokenPath, JsonSerializer.Serialize(t)); } catch (Exception ex) { log.LogDebug(ex, "discord: could not save the login"); }
        return t;
    }

    // Voice channels per server, kept between refreshes: a server Discord does not answer for right now keeps its last list.
    private readonly Dictionary<string, List<DiscordChannel>> _guildChannels = [];
    private readonly Dictionary<string, string?> _guildIcons = [];

    [System.Text.RegularExpressions.GeneratedRegex(@"/icons/\d+/([A-Za-z0-9_]{1,64})\.")]
    private static partial System.Text.RegularExpressions.Regex IconUrl();

    // Server icons and avatars come from Discord's image host. The phone asks the agent for them (and never talks to
    // Discord itself); the agent fetches each image once and keeps it in memory.
    private static readonly ConcurrentDictionary<string, (byte[] Data, string Type)> ImageCache = new();
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>kind is "guild" or "user"; id and hash are checked here, because they build the URL.</summary>
    public static async Task<(byte[] Data, string Type)?> ImageAsync(string kind, string id, string hash, CancellationToken ct)
    {
        if (kind is not ("guild" or "user") || id.Length is 0 or > 24 || !id.All(char.IsAsciiDigit)
            || hash.Length is 0 or > 64 || !hash.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;
        var key = $"{kind}/{id}/{hash}";
        if (ImageCache.TryGetValue(key, out var hit)) return hit;
        try
        {
            using var res = await ImageHttp.GetAsync($"https://cdn.discordapp.com/{(kind == "guild" ? "icons" : "avatars")}/{id}/{hash}.png?size=64", ct);
            if (!res.IsSuccessStatusCode) return null;
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            if (data.Length is 0 or > 512 * 1024) return null;
            if (ImageCache.Count > 300) ImageCache.Clear();
            return ImageCache[key] = (data, "image/png");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Refreshes the voice channel list; returns false when some server did not answer, so the caller retries soon.</summary>
    private async Task<bool> RefreshVoiceChannels(CancellationToken ct)
    {
        var complete = true;
        try
        {
            var guilds = (await Request("GET_GUILDS", null, null, ct))?["guilds"]?.AsArray() ?? [];
            var seen = new HashSet<string>();
            log.LogDebug("discord: {N} servers reported", guilds.Count);
            foreach (var g in guilds.Take(60))
            {
                var gid = g?["id"]?.GetValue<string>(); var gname = g?["name"]?.GetValue<string>();
                if (gid is null) continue;
                seen.Add(gid);
                try
                {
                    if (!_guildIcons.ContainsKey(gid))
                    {
                        // GET_GUILD carries the icon url; asked once per server
                        var url = (await Request("GET_GUILD", new JsonObject { ["guild_id"] = gid }, null, ct))?["icon_url"]?.GetValue<string>();
                        _guildIcons[gid] = url is not null && IconUrl().Match(url) is { Success: true } m ? m.Groups[1].Value : null;
                    }
                    var icon = _guildIcons[gid];
                    var chans = (await Request("GET_CHANNELS", new JsonObject { ["guild_id"] = gid }, null, ct))?["channels"]?.AsArray() ?? [];
                    _guildChannels[gid] = chans
                        .Where(c => c?["type"]?.GetValue<int>() is 2 or 13 && c["id"] is not null)
                        .Select(c => new DiscordChannel(c!["id"]!.GetValue<string>(), c["name"]?.GetValue<string>() ?? c["id"]!.GetValue<string>(), gname, gid, icon)).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { complete = false; log.LogDebug("discord: channels of {Guild} failed: {Msg}", gname, ex.Message); }
            }
            // Discord sometimes reports only the servers it has loaded; keep what we saw (cleared on reconnect) instead of dropping them.
            if (seen.Count == 0) complete = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { complete = false; log.LogDebug("discord: listing servers failed: {Msg}", ex.Message); }
        return complete;
    }

    private async Task ReadLoop(Stream io, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await ReadFrame(io, ct);
            if (msg is null) break;
            var evt = msg["evt"]?.GetValue<string>();
            if (evt == "NOTIFICATION_CREATE")
            {
                var d = msg["data"];
                var title = d?["title"]?.GetValue<string>() ?? "";
                var body = d?["body"]?.GetValue<string>() ?? d?["message"]?["content"]?.GetValue<string>() ?? "";
                lock (_lock) { _notes.Insert(0, new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), title, body)); if (_notes.Count > 20) _notes.RemoveAt(20); }
                continue;
            }
            var id = msg["nonce"]?.GetValue<string>();
            if (id is null || !_pending.TryRemove(id, out var tcs)) continue;
            if (evt == "ERROR") tcs.TrySetException(new Exception(msg["data"]?["message"]?.GetValue<string>() ?? "Discord refused the request"));
            else tcs.TrySetResult(msg["data"]?.DeepClone());
        }
    }

    private async Task<JsonNode?> Request(string cmd, JsonObject? args, string? evt, CancellationToken ct, TimeSpan? wait = null)
    {
        var io = _io ?? throw new Exception("Discord is not connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["cmd"] = cmd, ["nonce"] = id, ["args"] = args ?? new JsonObject() };
        if (evt is not null) msg["evt"] = evt;
        // The deadline covers waiting for the send lock, the write and the answer, so a stalled pipe cannot block forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(5));
        await using var registration = timeout.Token.Register(() => { if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException("Discord did not answer")); });
        try
        {
            await _send.WaitAsync(timeout.Token);
            try { await WriteFrame(io, 1, msg, timeout.Token); } finally { _send.Release(); }
            return await tcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Discord did not answer"); }
        finally { _pending.TryRemove(id, out _); }
    }

    // One button press at a time, so two quick toggles each start from the state the previous one produced.
    private readonly SemaphoreSlim _act = new(1, 1);
    private int _voiceRev; // bumped each time a button press publishes new mute/deafen state

    /// <summary>Publishes a poll. A button press that finished since the poll read <paramref name="rev"/> is newer than what the poll saw, so it keeps its mute/deafen state.</summary>
    internal void PublishPoll(int rev, bool muted, bool deafened, DiscordSnapshot snapshot)
    {
        lock (_lock)
        {
            var stale = rev != _voiceRev;
            _snap = snapshot with { Muted = stale ? _snap.Muted : muted, Deafened = stale ? _snap.Deafened : deafened };
        }
    }

    internal int VoiceRevision => Volatile.Read(ref _voiceRev);

    /// <summary>The widget's buttons: mute, deafen, leave, join (a voice channel id from the list the agent already sent).</summary>
    public async Task<string?> ActAsync(string action, string? channel)
    {
        if (!ControlAllowed(config.Get()))
            return "controlling Discord is turned off in the widget's settings";
        await _act.WaitAsync();
        try
        {
            // Checked again here: the widget's option or page may have been switched off while this press was queued.
            if (!ControlAllowed(config.Get())) return "controlling Discord is turned off in the widget's settings";
            var s = _snap;
            if (!s.Connected) return "Discord is not connected";
            switch (action)
            {
                case "mute": ApplyVoice(await Request("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = !s.Muted }, null, CancellationToken.None)); break;
                case "deafen": ApplyVoice(await Request("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = !s.Deafened }, null, CancellationToken.None)); break;
                case "leave": await Request("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null }, null, CancellationToken.None); break;
                case "join" when !string.IsNullOrEmpty(channel) && s.Channels.Any(c => c.Id == channel):
                    await Request("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = channel, ["force"] = true }, null, CancellationToken.None); break;
                default: return "unknown action";
            }
            log.LogInformation("discord: {Action} {Channel}", action, channel);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _act.Release(); }
    }

    /// <summary>Control needs a Discord widget with the control option on, on a page that is enabled.</summary>
    public static bool ControlAllowed(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == "discord" && w.Bool("control") == true);

    // Discord answers a settings change with the new settings; take the state from that, not from the next poll.
    internal void ApplyVoice(JsonNode? vs)
    {
        if (vs is null) return;
        lock (_lock)
        {
            var s = _snap;
            _voiceRev++;
            _snap = s with { Muted = vs["mute"]?.GetValue<bool>() ?? s.Muted, Deafened = vs["deaf"]?.GetValue<bool>() ?? s.Deafened };
        }
    }

    // ---- IPC transport: 4-byte opcode + 4-byte length (little endian) + UTF-8 JSON ----

    private static async Task<Stream> OpenIpc(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(500, ct);
                    return pipe;
                }
                var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await sock.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(dir, $"discord-ipc-{i}")), ct); }
                catch { sock.Dispose(); throw; }
                return new NetworkStream(sock, true);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException) { }
        }
        throw new IOException("Discord is not running");
    }

    private static async Task WriteFrame(Stream io, int op, JsonObject body, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var buf = new byte[8 + json.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), json.Length);
        json.CopyTo(buf, 8);
        await io.WriteAsync(buf, ct);
        await io.FlushAsync(ct);
    }

    private static async Task<JsonNode?> ReadFrame(Stream io, CancellationToken ct)
    {
        var head = new byte[8];
        if (!await ReadExact(io, head, ct)) return null;
        var op = BitConverter.ToInt32(head, 0); var len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1 << 20) throw new IOException("Discord sent something unexpected");
        var body = new byte[len];
        if (!await ReadExact(io, body, ct)) return null;
        if (op == 2) return null; // close
        return JsonNode.Parse(body);
    }

    private static async Task<bool> ReadExact(Stream io, byte[] buf, CancellationToken ct)
    {
        for (var got = 0; got < buf.Length;)
        {
            var n = await io.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
