using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sidelight.Agent;

/// <summary>
/// The Second screen widget on Linux with Wayland, where no program may read the screen or move the pointer on its own.
/// The desktop's screen-sharing portal (xdg-desktop-portal: GNOME, KDE and others) asks the user once which monitor to
/// share, or creates a virtual monitor, and hands out a PipeWire stream that GStreamer's pipewiresrc turns into the same
/// JPEG frames ffmpeg gives on Windows and X11. With the widget's "Click and drag on the screen" switch on, the session
/// is a RemoteDesktop one that also allows the pointer, so taps become clicks through the portal (xdotool can't on
/// Wayland). The portal's restore token is kept (wayland-screen.json beside config.json, owner-only) so the dialog isn't shown again;
/// when the user refuses, or stops the sharing from the desktop's own indicator, nothing is asked again until they
/// press Allow in Settings › Sources › Second screen. A session runs while a widget shows the screen, or, with the
/// virtual monitor on, for as long as the agent runs (the virtual monitor lives only as long as its session).
/// </summary>
public sealed class WaylandScreen : IHostedService, IDisposable
{
    internal const string PortalName = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop";
    internal const string ScreenCastIface = "org.freedesktop.portal.ScreenCast", RemoteDesktopIface = "org.freedesktop.portal.RemoteDesktop";
    internal const string RequestIface = "org.freedesktop.portal.Request", SessionIface = "org.freedesktop.portal.Session";
    internal const uint SourceMonitor = 1, SourceVirtual = 4, DevicePointer = 2, CursorEmbedded = 2, PersistUntilRevoked = 2;
    internal const int ButtonLeft = 0x110, ButtonRight = 0x111; // evdev BTN_LEFT, BTN_RIGHT
    public const int DefaultWidth = 1600, DefaultHeight = 720;
    internal const int MinSize = 320, MaxSize = 7680;
    internal const long ConsentAfterMs = 1500; // a start still waiting this long is waiting on the portal's dialog
    internal const long RetryMs = 30_000;
    internal static readonly TimeSpan DialogTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The shared monitor's PipeWire node and its size in the desktop's logical pixels. <paramref name="SizeKnown"/>: the
    /// portal reported the size (a virtual monitor's comes from us), which its pointer calls need.
    /// </summary>
    public sealed record ScreenStream(uint Node, int Width, int Height, bool Virtual, bool SizeKnown = true, string? MappingId = null);

    /// <summary>What Settings › Sources › Second screen shows.</summary>
    public sealed record Info(bool Portal, bool VirtualAvailable, bool Virtual, int Width, int Height, bool Refused, bool Remembered, string State);

    /// <summary>Kept between runs: the restore tokens (one per kind of session), the virtual monitor and a refusal.</summary>
    internal sealed class Saved
    {
        public bool Virtual { get; set; }
        public int Width { get; set; } = DefaultWidth;
        public int Height { get; set; } = DefaultHeight;
        public bool Refused { get; set; }
        public Dictionary<string, string> Tokens { get; set; } = [];
    }

    private sealed class PortalRefused : Exception;

    private readonly ConfigStore _config;
    private readonly ILogger<WaylandScreen> _log;
    private readonly string _statePath;
    private readonly Lock _gate = new();
    private Saved _saved;
    private IDBus? _bus;
    private string? _session;
    private ScreenStream? _stream;
    private IScreenPointer? _pointer;
    private string? _kind;
    private Task? _opening;
    private int _attempt;
    private long _openingSince, _retryAt;
    private bool _disposed;

    internal Func<CancellationToken, Task<IDBus>> Connect { get; set; } = async ct => await DBusConnection.ConnectSessionAsync(ct);
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;
    internal Func<bool> IsWayland { get; set; } = () => ScreenShare.Platform() == "wayland";
    internal Func<string?> FindGst { get; set; } = () => ToolProcess.Find("gst-launch-1.0");
    /// <summary>libei on the portal's socket, for a stream whose size the portal doesn't know; the tests replace it.</summary>
    internal Func<int, string?, IScreenPointer?> StartEis { get; set; } = (fd, mapping) => EisPointer.Start(fd, mapping);
    /// <summary>Runs GStreamer once to give a new virtual monitor its size; the tests replace it.</summary>
    internal Func<string, IReadOnlyList<string>, Task> SizeVirtual { get; set; } = RunOnce;

    public WaylandScreen(ConfigStore config, ILogger<WaylandScreen> log)
        : this(config, log, Path.Combine(Path.GetDirectoryName(config.Path) ?? AppPaths.ConfigDir, "wayland-screen.json")) { }

    internal WaylandScreen(ConfigStore config, ILogger<WaylandScreen> log, string statePath)
    {
        _config = config;
        _log = log;
        _statePath = statePath;
        _saved = Load(statePath);
    }

    // ------------------------------------------------------------------ the session

    /// <summary>
    /// The stream to capture, starting the portal session when there is none (the dialog then shows on the computer).
    /// States: "live" with a stream; "starting"; "consent" (waiting on the dialog); "denied" (refused or stopped by the
    /// user); "failed" (the portal said no for another reason, tried again later); "unsupported" (not Wayland).
    /// </summary>
    public (string State, ScreenStream? Stream) Ensure()
    {
        if (!IsWayland()) return ("unsupported", null);
        var kind = WantedKind();
        var now = Now();
        IDBus? stale;
        lock (_gate)
        {
            if (_disposed) return ("starting", null);
            if (_saved.Refused) return ("denied", null);
            if (_stream is { } s && _kind == kind && _bus is { Connected: true }) return ("live", s);
            if (_opening is not null) return (now - _openingSince >= ConsentAfterMs ? "consent" : "starting", null);
            if (now < _retryAt) return ("failed", null);
            stale = DetachLocked();
            var attempt = ++_attempt;
            _openingSince = now;
            _opening = Task.Run(() => OpenAsync(kind, attempt));
        }
        CloseQuietly(stale);
        return ("starting", null);
    }

    /// <summary>Nothing shows the screen any more: the session ends, unless it holds the virtual monitor.</summary>
    public void Idle()
    {
        IDBus? stale = null;
        lock (_gate)
        {
            if (_saved.Virtual && !_saved.Refused) return;
            if (_opening is null) stale = DetachLocked();
        }
        CloseQuietly(stale);
    }

    /// <summary>"monitor" or "virtual", plus "+pointer" while a Second screen widget on an enabled page may click.</summary>
    internal string WantedKind()
    {
        bool isVirtual;
        lock (_gate) isVirtual = _saved.Virtual;
        var pointer = _config.Get().Widgets(enabledPagesOnly: true)
            .Any(w => w.Type == ScreenShare.WidgetType && w.Bool("share") == true && w.Bool("control") == true);
        return (isVirtual ? "virtual" : "monitor") + (pointer ? "+pointer" : "");
    }

    private async Task OpenAsync(string kind, int attempt)
    {
        IDBus? bus = null;
        try
        {
            using var cts = new CancellationTokenSource(DialogTimeout + TimeSpan.FromSeconds(30));
            bus = await Connect(cts.Token);
            var opened = await StartSessionAsync(bus, kind, cts.Token);
            if (opened.Stream.Virtual && FindGst() is { } gst)
                await SizeVirtual(gst, SizingArgs(opened.Stream));
            var b = bus;
            bus.Disconnected += () => Lost(b, closedByUser: false);
            await bus.SubscribeAsync(opened.Session, SessionIface, "Closed", _ => Lost(b, closedByUser: true), cts.Token);
            var pointer = opened.Pointer ? await PointerFor(bus, opened.Session, opened.Stream, cts.Token) : null;
            lock (_gate)
            {
                if (_disposed || attempt != _attempt) { pointer?.Dispose(); CloseQuietly(bus, opened.Session); bus = null; return; }
                if (opened.Token is { Length: > 0 } token) _saved.Tokens[kind] = token;
                else _saved.Tokens.Remove(kind);
                SaveLocked();
                (_bus, _session, _stream, _pointer, _kind) = (bus, opened.Session, opened.Stream, pointer, kind);
                _retryAt = 0;
                bus = null;
            }
            _log.LogInformation("screen: Wayland {Kind} shared ({W}x{H})", kind, opened.Stream.Width, opened.Stream.Height);
        }
        catch (PortalRefused)
        {
            lock (_gate)
            {
                if (attempt == _attempt) { _saved.Refused = true; _saved.Tokens.Remove(kind); SaveLocked(); }
            }
            _log.LogInformation("screen: sharing was refused on the computer");
        }
        catch (Exception ex)
        {
            lock (_gate) if (attempt == _attempt) _retryAt = Now() + RetryMs;
            _log.LogWarning("screen: the screen-sharing portal failed: {Msg}", ex is DBusException ? ex.Message : ex.GetType().Name);
        }
        finally
        {
            CloseQuietly(bus);
            lock (_gate) if (attempt == _attempt) _opening = null;
        }
    }

    internal sealed record Opened(string Session, ScreenStream Stream, bool Pointer, string? Token);

    /// <summary>
    /// The portal's own pointer calls where it knows the stream's size; otherwise (GNOME's virtual monitor) libei on the
    /// socket from ConnectToEIS, which places the pointer by the stream's region. Null when neither can be had.
    /// </summary>
    private async Task<IScreenPointer?> PointerFor(IDBus bus, string session, ScreenStream stream, CancellationToken ct)
    {
        if (stream.SizeKnown) return new PortalPointer(bus, session, stream);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            var r = await bus.CallAsync(PortalName, PortalPath, RemoteDesktopIface, "ConnectToEIS", "oa{sv}",
                [new DBusPath(session), new Dictionary<string, object>()], timeout.Token);
            if (r.FirstOrDefault() is not DBusFd { Fd: >= 0 } fd) return null;
            var eis = StartEis(fd.Fd, stream.MappingId);
            if (eis is null) _log.LogInformation("screen: clicking on the virtual monitor needs libei (libei1)");
            return eis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogInformation("screen: no libei socket from the portal ({Msg})", ex is DBusException ? ex.Message : ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The portal's dance: CreateSession, (SelectDevices,) SelectSources, Start, each answered by a Response signal on
    /// its own request path. A RemoteDesktop session carries the screen cast too; its restore token is given to
    /// SelectDevices, a plain ScreenCast session's to SelectSources.
    /// </summary>
    internal async Task<Opened> StartSessionAsync(IDBus bus, string kind, CancellationToken ct)
    {
        var pointer = kind.EndsWith("+pointer", StringComparison.Ordinal);
        var isVirtual = kind.StartsWith("virtual", StringComparison.Ordinal);
        var iface = pointer ? RemoteDesktopIface : ScreenCastIface;
        string? token;
        int width, height;
        lock (_gate) { _saved.Tokens.TryGetValue(kind, out token); (width, height) = (_saved.Width, _saved.Height); }
        Dictionary<string, object> Persist(Dictionary<string, object> o)
        {
            o["persist_mode"] = PersistUntilRevoked;
            if (token is { Length: > 0 }) o["restore_token"] = token;
            return o;
        }

        var created = await RequestAsync(bus, iface, "CreateSession", "a{sv}", [],
            new() { ["session_handle_token"] = "sidelight" + RandomToken() }, ct);
        var session = created.TryGetValue("session_handle", out var sh) ? sh.ToString() ?? "" : "";
        if (session.Length == 0) throw new IOException("no session");
        try
        {
            var path = new DBusPath(session);
            if (pointer)
                await RequestAsync(bus, RemoteDesktopIface, "SelectDevices", "oa{sv}", [path], Persist(new() { ["types"] = DevicePointer }), ct);
            var sources = new Dictionary<string, object>
            {
                ["types"] = isVirtual ? SourceVirtual : SourceMonitor, ["multiple"] = false, ["cursor_mode"] = CursorEmbedded,
            };
            await RequestAsync(bus, ScreenCastIface, "SelectSources", "oa{sv}", [path], pointer ? sources : Persist(sources), ct);
            var started = await RequestAsync(bus, iface, "Start", "osa{sv}", [path, ""], [], ct);
            var stream = ParseStreams(started, isVirtual, width, height).FirstOrDefault() ?? throw new IOException("no stream");
            var devices = started.TryGetValue("devices", out var d) && d is uint u ? u : 0;
            return new Opened(session, stream, pointer && (devices & DevicePointer) != 0, started.TryGetValue("restore_token", out var t) ? t as string : null);
        }
        catch
        {
            CloseSession(bus, session);
            throw;
        }
    }

    /// <summary>The streams of a Start response: a(ua{sv}), the node and its "size" (absent for a virtual monitor).</summary>
    internal static List<ScreenStream> ParseStreams(Dictionary<object, object> results, bool isVirtual, int width, int height)
    {
        var list = new List<ScreenStream>();
        if (!results.TryGetValue("streams", out var raw) || raw is not List<object> streams) return list;
        foreach (var item in streams)
        {
            if (item is not object[] { Length: 2 } pair || pair[0] is not uint node || pair[1] is not Dictionary<object, object> props) continue;
            var (w, h) = isVirtual ? (width, height) : (0, 0);
            var known = false;
            if (props.TryGetValue("size", out var size) && size is object[] { Length: 2 } wh && wh[0] is int sw && wh[1] is int shh && sw > 0 && shh > 0)
                (w, h, known) = (sw, shh, true);
            var mapping = props.TryGetValue("mapping_id", out var m) ? m as string : null;
            if (w > 0 && h > 0) list.Add(new ScreenStream(node, w, h, isVirtual, known, mapping));
        }
        return list;
    }

    /// <summary>One portal call that answers with a Response signal: 0 is done, 1 the user said no, anything else failed.</summary>
    private static async Task<Dictionary<object, object>> RequestAsync(IDBus bus, string iface, string method, string signature,
        object[] args, Dictionary<string, object> options, CancellationToken ct)
    {
        var handle = "sidelight" + RandomToken();
        options["handle_token"] = handle;
        var path = $"{PortalPath}/request/{bus.UniqueName.TrimStart(':').Replace('.', '_')}/{handle}";
        var answer = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = await bus.SubscribeAsync(path, RequestIface, "Response", body => answer.TrySetResult(body), ct);
        using (var call = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            call.CancelAfter(CallTimeout);
            await bus.CallAsync(PortalName, PortalPath, iface, method, signature, [.. args, options], call.Token);
        }
        object[] body;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(DialogTimeout);
            try { body = await answer.Task.WaitAsync(wait.Token); }
            catch (OperationCanceledException)
            {
                // nobody answered the dialog: take it away
                _ = bus.CallAsync(PortalName, path, RequestIface, "Close", "", [], CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
                throw new TimeoutException("nobody answered the screen-sharing dialog");
            }
        }
        var response = body.Length > 0 && body[0] is uint r ? r : 2;
        if (response == 1) throw new PortalRefused();
        if (response != 0) throw new IOException($"{method} failed");
        return body.Length > 1 && body[1] is Dictionary<object, object> results ? results : [];
    }

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>The session ended without us: the user stopped sharing (Closed), or the bus went away.</summary>
    private void Lost(IDBus bus, bool closedByUser)
    {
        IDBus? stale = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_bus, bus)) return;
            if (closedByUser) { _saved.Refused = true; SaveLocked(); }
            stale = DetachLocked();
        }
        CloseQuietly(stale);
        if (closedByUser) _log.LogInformation("screen: sharing was stopped on the computer");
    }

    /// <summary>Forgets the current session (the caller closes the returned connection outside the lock).</summary>
    private IDBus? DetachLocked()
    {
        var bus = _bus;
        _pointer?.Dispose();
        if (bus is not null && _session is { } session) CloseSession(bus, session);
        (_bus, _session, _stream, _pointer, _kind) = (null, null, null, null, null);
        return bus;
    }

    private static void CloseSession(IDBus bus, string session)
    {
        if (!bus.Connected) return;
        try
        {
            _ = bus.CallAsync(PortalName, session, SessionIface, "Close", "", [], CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (Exception) { }
    }

    /// <summary>Closing the connection ends every portal session it holds, so a lost Close call can't leave one behind.</summary>
    private static void CloseQuietly(IDBus? bus, string? session = null)
    {
        if (bus is null) return;
        if (session is not null) CloseSession(bus, session);
        _ = Task.Delay(500).ContinueWith(_ => { try { bus.Dispose(); } catch (Exception) { } }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ the pointer

    /// <summary>A touch on the shared monitor, through the portal: <paramref name="x"/>, <paramref name="y"/> 0..1 across it.</summary>
    public async Task<(int Status, string? Error)> PointerAsync(string action, double x, double y, int delta)
    {
        IScreenPointer? pointer;
        bool shared, asked;
        lock (_gate) (pointer, shared, asked) = (_pointer, _stream is not null, _kind?.EndsWith("+pointer", StringComparison.Ordinal) == true);
        if (!shared) return (409, "the screen isn't shared yet");
        if (pointer is null)
            return asked ? (501, "the computer didn't allow the pointer, or clicking on its virtual monitor needs libei (libei1)")
                : (409, "the sharing session is starting again with clicking allowed");
        try
        {
            await pointer.Move(x, y);
            switch (action)
            {
                case "click": await pointer.Button(ButtonLeft, true); await pointer.Button(ButtonLeft, false); break;
                case "down": await pointer.Button(ButtonLeft, true); break;
                case "up": await pointer.Button(ButtonLeft, false); break;
                case "rightclick": await pointer.Button(ButtonRight, true); await pointer.Button(ButtonRight, false); break;
                case "scroll":
                    // the panel's delta is Windows' (120 a notch, positive away from you); the portal counts notches toward you
                    var steps = -(int)Math.Round(delta / 120.0, MidpointRounding.AwayFromZero);
                    if (steps != 0) await pointer.Scroll(steps);
                    break;
            }
            return (200, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("screen: portal pointer failed ({Type})", ex is DBusException ? ex.Message : ex.GetType().Name);
            return (500, "the computer refused the click");
        }
    }

    // ------------------------------------------------------------------ GStreamer

    /// <summary>
    /// gst-launch-1.0's arguments for the shared stream: JPEGs at most a quality's width on stdout. pipewiresrc resends
    /// the last picture every second, since the desktop sends a frame only when something on the monitor changes. A
    /// virtual monitor's size is pinned first, or the scaling would make the desktop resize the monitor itself.
    /// </summary>
    internal static List<string> GstArgs(ScreenStream s, int fps, string? quality)
    {
        var (maxWidth, q) = quality switch { "low" => (960, 60), "high" => (1920, 88), _ => (1280, 75) };
        var ci = CultureInfo.InvariantCulture;
        var (w, h) = Scaled(s.Width, s.Height, maxWidth);
        List<string> args = ["-q", "pipewiresrc", $"path={s.Node.ToString(ci)}", "always-copy=true", "keepalive-time=1000", "!", "videoconvert"];
        if (s.Virtual) args.AddRange(["!", $"video/x-raw,width={s.Width.ToString(ci)},height={s.Height.ToString(ci)}"]);
        args.AddRange(["!", "videoscale", "!", $"video/x-raw,width={w.ToString(ci)},height={h.ToString(ci)},pixel-aspect-ratio=1/1",
            "!", "videorate", "drop-only=true", $"max-rate={fps.ToString(ci)}", "!", "jpegenc", $"quality={q.ToString(ci)}", "!", "fdsink", "fd=1"]);
        return args;
    }

    /// <summary>Connects once with the virtual monitor's size, which the desktop then gives the monitor, and ends.</summary>
    internal static List<string> SizingArgs(ScreenStream s)
    {
        var ci = CultureInfo.InvariantCulture;
        return ["-q", "pipewiresrc", $"path={s.Node.ToString(ci)}", "num-buffers=1", "!", "videoconvert", "!",
            $"video/x-raw,width={s.Width.ToString(ci)},height={s.Height.ToString(ci)}", "!", "fakesink"];
    }

    /// <summary>A size no wider than <paramref name="maxWidth"/>, same shape, even numbers (JPEG's colour halves).</summary>
    internal static (int W, int H) Scaled(int width, int height, int maxWidth)
    {
        var w = Math.Min(width, maxWidth);
        var h = (int)Math.Round((double)height * w / width);
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }

    private static async Task RunOnce(string gst, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(gst);
        foreach (var a in args) psi.ArgumentList.Add(a);
        await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    // ------------------------------------------------------------------ settings

    public async Task<Info> InfoAsync(CancellationToken ct)
    {
        var (state, _) = IsWayland() ? Peek() : ("unsupported", null);
        uint types = 0;
        if (IsWayland())
        {
            try
            {
                using var bus = await Connect(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(CallTimeout);
                var r = await bus.CallAsync(PortalName, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss",
                    [ScreenCastIface, "AvailableSourceTypes"], timeout.Token);
                types = r.FirstOrDefault() is uint u ? u : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogDebug("screen: no screen-sharing portal ({Type})", ex.GetType().Name);
            }
        }
        lock (_gate)
            return new Info((types & SourceMonitor) != 0, (types & SourceVirtual) != 0, _saved.Virtual, _saved.Width, _saved.Height,
                _saved.Refused, _saved.Tokens.Count > 0, state);
    }

    /// <summary>The state without starting anything (the settings page only looks).</summary>
    private (string, ScreenStream?) Peek()
    {
        lock (_gate)
        {
            if (_saved.Refused) return ("denied", null);
            if (_stream is { } s) return ("live", s);
            if (_opening is not null) return ("consent", null);
            return ("idle", null);
        }
    }

    /// <summary>Turns the virtual monitor on (the portal then asks, on the computer) or off; the size is the phone's shape.</summary>
    public (int Status, string? Error) SetVirtual(bool on, int? width, int? height)
    {
        if (!IsWayland()) return (501, "a virtual monitor through the portal needs a Wayland session");
        IDBus? stale;
        lock (_gate)
        {
            _saved.Virtual = on;
            if (width is { } w) _saved.Width = Math.Clamp(w & ~1, MinSize, MaxSize);
            if (height is { } h) _saved.Height = Math.Clamp(h & ~1, MinSize / 2, MaxSize);
            if (on) _saved.Refused = false; // the button is the user asking for it
            _retryAt = 0;
            _attempt++;
            _opening = null;
            SaveLocked();
            stale = DetachLocked();
        }
        CloseQuietly(stale);
        if (on) Ensure();
        return (200, null);
    }

    /// <summary>Forgets the remembered answers: the next start shows the portal's dialog again (and may be allowed again).</summary>
    public void Forget()
    {
        IDBus? stale;
        lock (_gate)
        {
            _saved.Tokens.Clear();
            _saved.Refused = false;
            _retryAt = 0;
            _attempt++;
            _opening = null;
            SaveLocked();
            stale = DetachLocked();
        }
        CloseQuietly(stale);
        bool keep;
        lock (_gate) keep = _saved.Virtual;
        if (keep) Ensure();
    }

    private static Saved Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } s)
            {
                s.Tokens ??= [];
                s.Width = Math.Clamp(s.Width, MinSize, MaxSize);
                s.Height = Math.Clamp(s.Height, MinSize / 2, MaxSize);
                return s;
            }
        }
        catch (Exception) { }
        return new Saved();
    }

    private void SaveLocked()
    {
        try { AppPaths.WriteAllTextPrivate(_statePath, JsonSerializer.Serialize(_saved)); }
        catch (Exception ex) { _log.LogWarning("screen: couldn't save the Wayland screen state ({Type})", ex.GetType().Name); }
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>With the virtual monitor on it comes back when the agent starts (silently, with the restore token).</summary>
    public Task StartAsync(CancellationToken ct)
    {
        bool start;
        lock (_gate) start = _saved.Virtual && !_saved.Refused;
        if (start && IsWayland()) Ensure();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        IDBus? stale;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _attempt++;
            stale = DetachLocked();
        }
        if (stale is null) return;
        try { stale.Dispose(); } catch (Exception) { }
    }
}
