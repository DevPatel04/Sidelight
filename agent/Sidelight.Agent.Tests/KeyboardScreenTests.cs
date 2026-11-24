using Microsoft.Extensions.Logging.Abstractions;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// The Keyboard widget presses one tapped key at a time and only for a widget that allows it; the Second screen widget
/// shows and clicks a monitor only with its own switches on, and finds the monitor it is asked for.
/// </summary>
public sealed class KeyboardScreenTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sidelight-kbscreen-{Guid.NewGuid():N}.json");
    private readonly List<string> _typed = [];
    private readonly List<string> _keys = [];
    private FocusedApp? _focused = new("notepad", false);

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    private ConfigStore Store(string keyboardOptions = """{ "control": true }""", string screenOptions = """{ "share": true, "control": true }""")
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "p", "title": "P", "widgets": [
                { "id": "k", "type": "keyboard", "x": 0, "y": 0, "w": 12, "h": 3, "options": {{keyboardOptions}} },
                { "id": "s", "type": "screen", "x": 0, "y": 3, "w": 12, "h": 3, "options": {{screenOptions}} } ] } ] }
            """);
        return new ConfigStore(_path);
    }

    private KeyboardInput Keyboard(ConfigStore store) => new(store, NullLogger<KeyboardInput>.Instance)
    {
        TypeText = (text, _) => { _typed.Add(text); return true; },
        SendCombo = combo => { _keys.Add(string.Join("+", combo)); return true; },
        Focused = () => _focused,
        CheckElevation = true,
        AgentElevated = false,
    };

    [Fact]
    public async Task CharactersAreTypedAsTextAndOtherKeysAsOneCombo()
    {
        var kb = Keyboard(Store());
        Assert.Equal((200, (string?)null), await kb.PressAsync(null, "p/k", null, "?", default));
        Assert.Equal((200, (string?)null), await kb.PressAsync(null, "p/k", "Ctrl+Shift+backquote", null, default));
        Assert.Equal((200, (string?)null), await kb.PressAsync(null, "p/k", "ctrl+\\", null, default));
        Assert.Equal(["?"], _typed);
        Assert.Equal(["ctrl+shift+backquote", "ctrl+backslash"], _keys);
    }

    [Fact]
    public async Task NothingIsPressedWithoutTheWidgetsSwitchOrForAnotherWidget()
    {
        var kb = Keyboard(Store("""{ "control": false }"""));
        Assert.Equal(403, (await kb.PressAsync(null, "p/k", "enter", null, default)).Status);
        Assert.Equal(403, (await Keyboard(Store()).PressAsync(null, "p/s", "enter", null, default)).Status);
        Assert.Empty(_typed);
        Assert.Empty(_keys);
    }

    [Theory]
    [InlineData("ctrl+a, delete")] // a list would let one request run a macro
    [InlineData("ctrl+nosuchkey")]
    public async Task OnlyOneKnownComboIsTaken(string key)
    {
        Assert.Equal(400, (await Keyboard(Store()).PressAsync(null, "p/k", key, null, default)).Status);
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task ControlCharactersNeverArriveAsText()
    {
        var kb = Keyboard(Store());
        // a line break as text would be Enter in disguise
        Assert.Equal(400, (await kb.PressAsync(null, "p/k", null, "\n", default)).Status);
        await kb.PressAsync(null, "p/k", null, "a\r\tb", default);
        Assert.Equal(["ab"], _typed);
        Assert.Equal(413, (await kb.PressAsync(null, "p/k", null, new string('x', KeyboardInput.MaxText + 1), default)).Status);
    }

    [Fact]
    public async Task AnAdministratorWindowIsReportedInsteadOfSilentlyIgnored()
    {
        _focused = new FocusedApp("regedit", true);
        Assert.Equal(409, (await Keyboard(Store()).PressAsync(null, "p/k", "enter", null, default)).Status);
        Assert.Empty(_keys);
    }

    // ---- second screen

    private static readonly ScreenMonitor Main = new(0, 0, 1920, 1080, true);
    private static readonly ScreenMonitor Virtual = new(1920, 0, 1600, 720, false);
    private static readonly ScreenMonitor Left = new(-1280, 0, 1280, 1024, false);

    private sealed class FakeRun : ICameraRun
    {
        public bool HasEnded { get; set; }
        public string? LastError { get; set; }
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private (ScreenShare Screen, List<(FakeRun Run, IReadOnlyList<string> Args, Action<byte[]> Frame)> Runs, List<KeySender.MouseCall> Mouse, Action<long> Advance)
        Screen(ConfigStore store, string? platform = "windows", IReadOnlyList<ScreenMonitor>? monitors = null)
    {
        var runs = new List<(FakeRun, IReadOnlyList<string>, Action<byte[]>)>();
        var mouse = new List<KeySender.MouseCall>();
        long now = 1_000_000;
        var screen = new ScreenShare(store, NullLogger<ScreenShare>.Instance,
            (_, args, onFrame) => { var r = new FakeRun(); runs.Add((r, args, onFrame)); return r; },
            () => now, () => "ffmpeg", () => monitors ?? [Main, Virtual], () => platform, timer: false)
        { Mouse = mouse.Add };
        return (screen, runs, mouse, ms => now += ms);
    }

    [Fact]
    public void TheExtendedScreenIsTheFirstMonitorThatIsNotTheMainOne()
    {
        Assert.Equal(Virtual, ScreenShare.Choose([Virtual, Main], "auto"));
        Assert.Equal(Left, ScreenShare.Choose([Main, Virtual, Left], null));
        Assert.Equal(Main, ScreenShare.Choose([Main], "auto"));
        // counted from the left
        Assert.Equal(Main, ScreenShare.Choose([Main, Virtual, Left], "2"));
        Assert.Null(ScreenShare.Choose([Main], "2"));
        Assert.Null(ScreenShare.Choose([], "auto"));
    }

    [Fact]
    public void XrandrMonitorsAreRead()
    {
        const string output = """
            Monitors: 2
             0: +*eDP-1 1920/344x1080/194+0+0  eDP-1
             1: +VIRTUAL1 1600/423x720/190+1920+0  VIRTUAL1
            """;
        Assert.Equal([Main, Virtual], ScreenShare.ParseXrandr(output));
    }

    [Fact]
    public void TheCaptureGrabsExactlyThatMonitor()
    {
        var win = ScreenShare.CaptureArgs("windows", Virtual, 8, null);
        Assert.Equal(["-offset_x", "1920", "-offset_y", "0", "-video_size", "1600x720", "-i", "desktop"],
            win.SkipWhile(a => a != "-offset_x").Take(8));
        var x11 = ScreenShare.CaptureArgs("x11 :0", Left, 8, "low");
        Assert.Contains(":0+-1280,0", x11);
        Assert.Contains("scale='min(960,iw)':-2", x11);
    }

    [Fact]
    public void NoPictureWithoutTheWidgetsSwitchOrOnAnUnsupportedSystem()
    {
        var (off, offRuns, _, _) = Screen(Store(screenOptions: """{ "share": false }"""));
        Assert.Equal((403, (byte[]?)null, "off"), off.Frame(null, "p/s"));
        Assert.Equal((404, (byte[]?)null, "unknown"), off.Frame(null, "p/k"));
        var (wayland, _, _, _) = Screen(Store(), platform: null);
        Assert.Equal((501, (byte[]?)null, "unsupported"), wayland.Frame(null, "p/s"));
        var (none, _, _, _) = Screen(Store(), monitors: []);
        Assert.Equal((404, (byte[]?)null, "no-monitor"), none.Frame(null, "p/s"));
        Assert.Empty(offRuns);
    }

    [Fact]
    public void APictureIsServedOnceAndTheCaptureStopsWhenNobodyAsks()
    {
        var (screen, runs, _, advance) = Screen(Store());
        Assert.Equal(503, screen.Frame(null, "p/s").Status); // starting
        var (run, args, frame) = Assert.Single(runs);
        Assert.Contains("1600x720", args);
        frame([0xFF, 0xD8, 0xFF, 0xD9]);
        Assert.Equal(200, screen.Frame(null, "p/s").Status);
        Assert.Equal(204, screen.Frame(null, "p/s").Status); // this widget has that picture already
        Assert.Equal(200, screen.Frame(null, "p/s", fresh: true).Status);
        advance(ScreenShare.KeepMs + 1);
        screen.Tick();
        Assert.True(run.Disposed);
    }

    [Fact]
    public void TouchesBecomeClicksOnThatMonitor()
    {
        var (screen, _, mouse, _) = Screen(Store());
        Assert.Equal((200, (string?)null), screen.Pointer(null, "p/s", "click", 0.5, 1, null));
        Assert.Equal([new KeySender.MouseCall("moveTo", 1920 + 800, 719, 0), new KeySender.MouseCall("click", 0, 0, 0)], mouse);
        mouse.Clear();
        screen.Pointer(null, "p/s", "scroll", -3, 0, 99_999); // clamped onto the monitor and the scroll limit
        Assert.Equal([new KeySender.MouseCall("moveTo", 1920, 0, 0), new KeySender.MouseCall("scroll", 0, 0, 1000)], mouse);
    }

    [Fact]
    public void TouchesNeedBothSwitches()
    {
        var (viewOnly, _, mouse, _) = Screen(Store(screenOptions: """{ "share": true, "control": false }"""));
        Assert.Equal(403, viewOnly.Pointer(null, "p/s", "click", 0.5, 0.5, null).Status);
        var (hidden, _, mouse2, _) = Screen(Store(screenOptions: """{ "share": false, "control": true }"""));
        Assert.Equal(403, hidden.Pointer(null, "p/s", "click", 0.5, 0.5, null).Status);
        var (screen, _, mouse3, _) = Screen(Store());
        Assert.Equal(400, screen.Pointer(null, "p/s", "click", double.NaN, 0.5, null).Status);
        Assert.Equal(400, screen.Pointer(null, "p/s", "launch", 0.5, 0.5, null).Status);
        Assert.Empty(mouse);
        Assert.Empty(mouse2);
        Assert.Empty(mouse3);
    }

    // ---- virtual display install (Settings › Sources › Second screen)

    private (VirtualDisplay Display, List<string> Ran, List<string> Opened) Display(string platform = "windows", string? winget = @"C:\winget.exe",
        bool installs = true)
    {
        string? control = null;
        var ran = new List<string>();
        var opened = new List<string>();
        var d = new VirtualDisplay(Store(), NullLogger<VirtualDisplay>.Instance)
        {
            PlatformName = () => platform,
            FindWinget = () => winget,
            FindControl = () => control,
            DriverPresent = () => false,
            Monitors = () => [Main],
            Run = (tool, args, _) => { ran.Add(tool + " " + string.Join(' ', args)); if (installs) control = @"C:\VDD\VDD Control.exe"; return Task.FromResult(installs ? 0 : 1); },
            Launch = exe => { opened.Add(exe); return true; },
        };
        return (d, ran, opened);
    }

    [Fact]
    public async Task InstallRunsWingetForExactlyThatPackageThenOpensVddControl()
    {
        var (d, ran, opened) = Display();
        Assert.Equal((200, (string?)null), await d.InstallAsync(default));
        var cmd = Assert.Single(ran);
        Assert.StartsWith(@"C:\winget.exe install --id VirtualDrivers.Virtual-Display-Driver --exact --source winget", cmd);
        Assert.Equal([@"C:\VDD\VDD Control.exe"], opened);
        // already there: just opened again, winget isn't run a second time
        await d.InstallAsync(default);
        Assert.Single(ran);
        Assert.Equal(2, opened.Count);
    }

    [Fact]
    public async Task InstallSaysWhatIsMissingInsteadOfGuessing()
    {
        var (noWinget, ran, _) = Display(winget: null);
        Assert.Equal(412, (await noWinget.InstallAsync(default)).Status);
        var (failed, _, opened) = Display(installs: false);
        Assert.Equal(500, (await failed.InstallAsync(default)).Status);
        Assert.Empty(opened);
        var (linux, linuxRan, _) = Display(platform: "wayland");
        Assert.Equal(501, (await linux.InstallAsync(default)).Status);
        Assert.Empty(ran);
        Assert.Empty(linuxRan);
        Assert.Equal(404, Display().Display.Open().Status); // nothing to open before the install
    }

    [Fact]
    public void TheStatusTellsTheSettingsPageWhatToOffer()
    {
        var st = Display().Display.Get();
        Assert.Equal("windows", st.Platform);
        Assert.True(st.Capture && st.Winget);
        Assert.False(st.ControlApp || st.Driver || st.Installing);
        var wayland = Display(platform: "wayland").Display.Get();
        Assert.False(wayland.Capture || wayland.Winget || wayland.Driver);
    }

    [Theory]
    [InlineData("Virtual Display Driver", true)]
    [InlineData("IddSampleDriver Device", true)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData(null, false)]
    public void VirtualAdaptersAreRecognized(string? name, bool expected) => Assert.Equal(expected, VirtualDisplay.IsVirtualAdapter(name));
}
