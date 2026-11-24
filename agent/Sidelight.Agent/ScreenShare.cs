using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sidelight.Agent;

/// <summary>One monitor of the computer, in physical desktop pixels.</summary>
public sealed record ScreenMonitor(int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Second screen widget: a live picture of one of the computer's monitors on the phone, and (with its own switch) the
/// phone's touches as clicks and drags on that monitor. With a virtual display added on the computer (docs/SECOND-SCREEN.md)
/// that monitor is an extra screen for windows that lives on the phone. ffmpeg captures the monitor (gdigrab on Windows,
/// x11grab on Linux with X11) as a few JPEG frames a second, like the cameras (<see cref="CameraFeeds"/>): only while a
/// widget on an enabled page keeps asking, stopping a few seconds after the last request. Both switches are off by
/// default, as the picture is everything on that screen.
/// </summary>
public sealed partial class ScreenShare : IDisposable
{
    public const string WidgetType = "screen";
    public const int DefaultFps = 8, MaxFps = 15;
    /// <summary>At most this many captures at the same time (two phones showing different monitors, say).</summary>
    public const int MaxRunning = 2;
    internal const long KeepMs = 8_000;       // a capture stops this long after the last request
    internal const long StallMs = 15_000;     // no frame for this long: ffmpeg is restarted
    internal const long FreshMs = 3_000;      // an older frame isn't shown as live
    internal const long MaxBackoffMs = 30_000;
    internal const long MonitorCacheMs = 5_000;
    private const int MaxScrollDelta = 1000;

    /// <summary>Starts a capture; frames go to the callback. Replaceable so the tests never start a process.</summary>
    internal delegate ICameraRun Launcher(string ffmpeg, IReadOnlyList<string> args, Action<byte[]> onFrame);

    private readonly ConfigStore _config;
    private readonly ILogger<ScreenShare> _log;
    private readonly Launcher _launch;
    private readonly Func<long> _now;
    private readonly Func<string?> _findFfmpeg;
    private readonly Func<IReadOnlyList<ScreenMonitor>> _listMonitors;
    private readonly Func<string?> _platform;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Feed> _feeds = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer _timer;
    private IReadOnlyList<ScreenMonitor> _monitors = [];
    private long _monitorsAt;
    private bool _monitorsListed;
    private bool _disposed;

    /// <summary>Where the phone's touches go; the tests replace it.</summary>
    internal Action<KeySender.MouseCall> Mouse { get; set; } = Dispatch;

    public ScreenShare(ConfigStore config, ILogger<ScreenShare> log) : this(config, log, null, null, null, null, null) { }

    internal ScreenShare(ConfigStore config, ILogger<ScreenShare> log, Launcher? launch, Func<long>? now, Func<string?>? findFfmpeg,
        Func<IReadOnlyList<ScreenMonitor>>? monitors, Func<string?>? platform, bool timer = true)
    {
        _config = config;
        _log = log;
        _launch = launch ?? ((ffmpeg, args, onFrame) => FfmpegRun.Launch(ffmpeg, args, onFrame));
        _now = now ?? (() => Environment.TickCount64);
        _findFfmpeg = findFfmpeg ?? (() => CameraFeeds.FindFfmpeg(_config.Get().Cameras.Ffmpeg));
        _listMonitors = monitors ?? ListMonitors;
        _platform = platform ?? Platform;
        _timer = new System.Threading.Timer(_ => { try { Tick(); } catch (Exception ex) { _log.LogDebug("screen: {Msg}", ex.Message); } }, null,
            timer ? 1000 : Timeout.Infinite, timer ? 1000 : Timeout.Infinite);
    }

    private sealed class Feed(string name)
    {
        public readonly string Name = name;
        public ICameraRun? Run;
        public byte[]? Frame;
        public long FrameAt, StartedAt, WantedUntil, RetryAt, Backoff, FrameSeq;
        public int Generation;
        public readonly Dictionary<string, long> Served = new(StringComparer.Ordinal);
        public bool GotFrame;
        public string State = "starting";
    }

    /// <summary>
    /// The latest picture for the Second screen widget the phone names ("pageId/widgetId"). 200 with a JPEG, or a status
    /// with one of the states the panel shows: "unknown" (no such widget on an enabled page), "off" (the widget's switch),
    /// "unsupported" (not Windows or Linux with X11), "no-ffmpeg", "no-monitor", "starting", "failed", "busy".
    /// </summary>
    public (int Status, byte[]? Jpeg, string State) Frame(string? deviceId, string? widgetKey, bool fresh = false)
    {
        var now = _now();
        if (PanelWidgets.Find(_config.Get(), deviceId, widgetKey, WidgetType) is not { } widget) return (404, null, "unknown");
        if (widget.Bool("share") != true) return (403, null, "off");
        if (_platform() is not { } platform) return (501, null, "unsupported");
        if (Pick(widget) is not { } monitor) return (404, null, "no-monitor");
        var args = CaptureArgs(platform, monitor, Fps(widget), widget.Str("quality"));
        var key = string.Join('\u0001', args);
        lock (_gate)
        {
            if (_disposed) return (503, null, "starting");
            if (!_feeds.TryGetValue(key, out var feed))
            {
                if (_feeds.Count >= MaxRunning) return (503, null, "busy");
                _feeds[key] = feed = new Feed($"{monitor.Width}x{monitor.Height}+{monitor.X}+{monitor.Y}");
            }
            feed.WantedUntil = now + KeepMs;
            if (feed.Run is null && now >= feed.RetryAt) StartLocked(feed, args, now);
            if (feed.Frame is { } jpeg && now - feed.FrameAt <= FreshMs)
            {
                var asker = (deviceId ?? "") + "\n" + widgetKey;
                if (!fresh && feed.Served.TryGetValue(asker, out var had) && had == feed.FrameSeq) return (204, null, "live");
                if (feed.Served.Count > 64) feed.Served.Clear();
                feed.Served[asker] = feed.FrameSeq;
                return (200, jpeg, "live");
            }
            return (503, null, feed.State);
        }
    }

    /// <summary>
    /// A touch on the widget's picture: <paramref name="x"/> and <paramref name="y"/> are 0..1 across the monitor shown.
    /// Actions: move, click, down, up, rightclick, scroll (with <paramref name="delta"/>, 120 a notch).
    /// </summary>
    public (int Status, string? Error) Pointer(string? deviceId, string? widgetKey, string? action, double? x, double? y, double? delta)
    {
        var widget = PanelWidgets.Find(_config.Get(), deviceId, widgetKey, WidgetType);
        if (widget?.Bool("share") != true || widget.Bool("control") != true)
            return (403, "clicking is off: turn on \"Click and drag on the screen\" in the second screen widget's settings");
        if (_platform() is null) return (501, "showing a screen needs Windows, or Linux with X11");
        if (Pick(widget) is not { } m) return (404, "that monitor is not connected");
        if (x is not { } fx || y is not { } fy || !double.IsFinite(fx) || !double.IsFinite(fy)) return (400, "no position");
        var (px, py) = ToDesktop(m, fx, fy);
        var d = delta is { } dv && double.IsFinite(dv) ? Math.Clamp((int)Math.Round(dv), -MaxScrollDelta, MaxScrollDelta) : 0;
        var calls = (action ?? "").ToLowerInvariant() switch
        {
            "move" => new[] { "moveTo" },
            "click" => ["moveTo", "click"],
            "down" => ["moveTo", "mouseDown"],
            "up" => ["moveTo", "mouseUp"],
            "rightclick" => ["moveTo", "rightClick"],
            "scroll" => ["moveTo", "scroll"],
            _ => null,
        };
        if (calls is null) return (400, "unknown action");
        try
        {
            foreach (var c in calls)
                Mouse(c == "moveTo" ? new KeySender.MouseCall(c, px, py, 0) : new KeySender.MouseCall(c, 0, 0, d));
            return (200, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("screen: pointer failed ({Type})", ex.GetType().Name);
            return (500, "the computer refused the click");
        }
    }

    /// <summary>A point 0..1 across a monitor, as a desktop pixel inside it.</summary>
    internal static (int X, int Y) ToDesktop(ScreenMonitor m, double x, double y) =>
        (m.X + (int)Math.Round(Math.Clamp(x, 0, 1) * (m.Width - 1)), m.Y + (int)Math.Round(Math.Clamp(y, 0, 1) * (m.Height - 1)));

    private static void Dispatch(KeySender.MouseCall c)
    {
        switch (c.Action)
        {
            case "moveTo": KeySender.MouseMoveTo(c.Dx, c.Dy); break;
            case "click": KeySender.MouseClick(); break;
            case "mouseDown": KeySender.MouseDown(); break;
            case "mouseUp": KeySender.MouseUp(); break;
            case "rightClick": KeySender.MouseRightClick(); break;
            case "scroll": KeySender.MouseScroll(c.Delta); break;
        }
    }

    private static int Fps(WidgetConfig w) =>
        w.Options is { } o && o.TryGetValue("fps", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDouble(out var f) && double.IsFinite(f)
            ? Math.Clamp((int)Math.Round(f), 1, MaxFps) : DefaultFps;

    /// <summary>
    /// The monitor a widget shows. "auto" (the default) is the first monitor that isn't the main one, from the left: the
    /// extended screen, virtual or real; with a single monitor, that one. "1", "2"… count the monitors from the left.
    /// </summary>
    internal ScreenMonitor? Pick(WidgetConfig w) => Choose(Monitors(), w.Str("monitor"));

    internal static ScreenMonitor? Choose(IReadOnlyList<ScreenMonitor> monitors, string? choice)
    {
        var ordered = monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        if (ordered.Count == 0) return null;
        if (int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n >= 1 && n <= ordered.Count ? ordered[n - 1] : null;
        return ordered.FirstOrDefault(m => !m.Primary) ?? ordered[0];
    }

    private IReadOnlyList<ScreenMonitor> Monitors()
    {
        var now = _now();
        lock (_gate)
        {
            if (_monitorsListed && now - _monitorsAt < MonitorCacheMs) return _monitors;
        }
        IReadOnlyList<ScreenMonitor> list;
        try { list = _listMonitors(); }
        catch (Exception ex) { _log.LogDebug("screen: listing monitors failed: {Msg}", ex.Message); list = []; }
        lock (_gate) { _monitors = list; _monitorsAt = now; _monitorsListed = true; }
        return list;
    }

    /// <summary>ffmpeg's arguments for one monitor: the platform's screen grabber in, JPEGs out at most a quality's width.</summary>
    internal static List<string> CaptureArgs(string platform, ScreenMonitor m, int fps, string? quality)
    {
        var (maxWidth, q) = quality switch { "low" => (960, 10), "high" => (1920, 4), _ => (1280, 7) };
        var ci = CultureInfo.InvariantCulture;
        var size = $"{m.Width.ToString(ci)}x{m.Height.ToString(ci)}";
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error"];
        if (platform == "windows")
            args.AddRange(["-f", "gdigrab", "-framerate", fps.ToString(ci), "-draw_mouse", "1",
                "-offset_x", m.X.ToString(ci), "-offset_y", m.Y.ToString(ci), "-video_size", size, "-i", "desktop"]);
        else
            args.AddRange(["-f", "x11grab", "-framerate", fps.ToString(ci), "-draw_mouse", "1", "-video_size", size,
                "-i", $"{platform["x11 ".Length..]}+{m.X.ToString(ci)},{m.Y.ToString(ci)}"]);
        args.AddRange(["-an", "-vf", $"scale='min({maxWidth.ToString(ci)},iw)':-2", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", q.ToString(ci), "pipe:1"]);
        return args;
    }

    /// <summary>"windows", "x11 :0" (the X display to grab), or null where the screen can't be captured (Wayland, macOS).</summary>
    internal static string? Platform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return null;
        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? "";
        return DisplayName().IsMatch(display) ? "x11 " + display : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]*:\d+(\.\d+)?$")]
    private static partial Regex DisplayName();

    // " 0: +*eDP-1 1920/344x1080/194+0+0  eDP-1": index, flags (+ active, * primary), name, WxH with millimetres, +X+Y
    [GeneratedRegex(@"^\s*\d+:\s+\+?(?<primary>\*?)\S+\s+(?<w>\d+)/\d+x(?<h>\d+)/\d+\+(?<x>-?\d+)\+(?<y>-?\d+)", RegexOptions.Multiline)]
    private static partial Regex XrandrMonitor();

    /// <summary>The monitors in <c>xrandr --listmonitors</c> output.</summary>
    internal static List<ScreenMonitor> ParseXrandr(string output) =>
        XrandrMonitor().Matches(output).Select(m => new ScreenMonitor(
            int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
            m.Groups["primary"].Value == "*")).Where(m => m.Width > 0 && m.Height > 0).ToList();

    internal static IReadOnlyList<ScreenMonitor> ListMonitors()
    {
        if (OperatingSystem.IsWindows()) return WindowsMonitors();
        if (ToolProcess.Find("xrandr") is not { } xrandr) return [];
        var psi = new ProcessStartInfo(xrandr);
        psi.ArgumentList.Add("--listmonitors");
        var result = ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        return result.ExitCode == 0 ? ParseXrandr(result.Output) : [];
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);

    /// <summary>The monitors in physical pixels: the thread is per-monitor DPI aware while it asks, like ffmpeg's gdigrab.</summary>
    private static List<ScreenMonitor> WindowsMonitors()
    {
        var list = new List<ScreenMonitor>();
        var old = SetThreadDpiAwarenessContext(-4);
        try
        {
            EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(monitor, ref info))
                {
                    var r = info.rcMonitor;
                    list.Add(new ScreenMonitor(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.dwFlags & 1) != 0));
                }
                return true;
            }, 0);
        }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
        return list;
    }

    /// <summary>Once a second: stops captures nobody asks for, restarts a stalled or ended ffmpeg (with a growing pause).</summary>
    internal void Tick()
    {
        var now = _now();
        List<ICameraRun> stop = [];
        lock (_gate)
        {
            foreach (var (key, feed) in _feeds.ToList())
            {
                if (now >= feed.WantedUntil)
                {
                    if (feed.Run is not null) { stop.Add(feed.Run); _log.LogInformation("screen {Name}: stopped (not on screen)", feed.Name); }
                    _feeds.Remove(key);
                    continue;
                }
                if (feed.Run is { } run)
                {
                    var stalled = now - (feed.GotFrame ? feed.FrameAt : feed.StartedAt) > StallMs;
                    if (!run.HasEnded && !stalled) continue;
                    _log.LogInformation("screen {Name}: {What} ({Detail})", feed.Name, run.HasEnded ? "ffmpeg ended" : "no picture for a while", run.LastError ?? "");
                    feed.State = "failed";
                    stop.Add(run);
                    feed.Run = null;
                    feed.Generation++;
                    feed.Frame = null;
                    feed.Backoff = Math.Min(MaxBackoffMs, feed.Backoff == 0 ? 2000 : feed.Backoff * 2);
                    feed.RetryAt = now + feed.Backoff;
                }
                else if (now >= feed.RetryAt) StartLocked(feed, key.Split('\u0001'), now);
            }
        }
        foreach (var r in stop) r.Dispose();
    }

    private void StartLocked(Feed feed, IReadOnlyList<string> args, long now)
    {
        var ffmpeg = _findFfmpeg();
        if (ffmpeg is null) { feed.State = "no-ffmpeg"; feed.RetryAt = now + 10_000; return; }
        try
        {
            feed.StartedAt = now;
            if (feed.State != "failed") feed.State = "starting";
            var f = feed;
            var generation = ++feed.Generation;
            feed.GotFrame = false;
            feed.Run = _launch(ffmpeg, args, jpeg =>
            {
                lock (_gate)
                {
                    if (f.Generation != generation || f.Run is null) return; // from an ffmpeg that was stopped since
                    f.Frame = jpeg; f.FrameAt = _now(); f.FrameSeq++; f.GotFrame = true; f.State = "live"; f.Backoff = 0;
                }
            });
            _log.LogInformation("screen {Name}: started", feed.Name);
        }
        catch (Exception ex)
        {
            feed.Run = null;
            feed.State = "no-ffmpeg";
            feed.RetryAt = now + 10_000;
            _log.LogWarning("screen {Name}: cannot start ffmpeg: {Msg}", feed.Name, ex.Message);
        }
    }

    public void Dispose()
    {
        List<ICameraRun> stop;
        lock (_gate)
        {
            _disposed = true;
            stop = _feeds.Values.Select(f => f.Run).OfType<ICameraRun>().ToList();
            _feeds.Clear();
        }
        _timer.Dispose();
        foreach (var r in stop) r.Dispose();
    }
}
