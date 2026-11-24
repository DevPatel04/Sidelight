using Microsoft.Extensions.Logging.Abstractions;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// The Second screen widget on Wayland: the D-Bus wire format, and the screen-sharing portal's dance against a scripted
/// bus (no real D-Bus): the dialog is asked for once and its answer remembered, a refusal is respected, the pointer is
/// asked for only when the widget may click, and a virtual monitor gets its size and its clicks through libei.
/// </summary>
public sealed class WaylandScreenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sidelight-wayland-{Guid.NewGuid():N}");
    private readonly List<FakeBus> _buses = [];

    public WaylandScreenTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string StatePath => Path.Combine(_dir, "wayland-screen.json");

    private ConfigStore Store(bool control = false)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""
            { "pages": [ { "id": "p", "title": "P", "widgets": [
                { "id": "s", "type": "screen", "x": 0, "y": 0, "w": 12, "h": 6, "options": { "share": true, "control": {{(control ? "true" : "false")}} } } ] } ] }
            """);
        return new ConfigStore(path);
    }

    private int _tokens;
    private readonly List<string> _sized = [];
    private readonly List<(int Fd, string? Mapping)> _eis = [];
    private readonly FakePointer _eisPointer = new();

    private WaylandScreen Wayland(ConfigStore store, Action<FakeBus>? script = null) => new(store, NullLogger<WaylandScreen>.Instance, StatePath)
    {
        Connect = _ =>
        {
            var bus = new FakeBus(() => $"tok-{Interlocked.Increment(ref _tokens)}");
            script?.Invoke(bus);
            lock (_buses) _buses.Add(bus);
            return Task.FromResult<IDBus>(bus);
        },
        IsWayland = () => true,
        FindGst = () => "/usr/bin/gst-launch-1.0",
        SizeVirtual = (_, args) => { lock (_sized) _sized.Add(string.Join(' ', args)); return Task.CompletedTask; },
        StartEis = (fd, mapping) => { _eis.Add((fd, mapping)); return _eisPointer; },
    };

    private static async Task<(string State, WaylandScreen.ScreenStream? Stream)> Settle(WaylandScreen w)
    {
        var until = Environment.TickCount64 + 5000;
        while (true)
        {
            var r = w.Ensure();
            if (r.State is not ("starting" or "consent") || Environment.TickCount64 > until) return r;
            await Task.Delay(10);
        }
    }

    private FakeBus.Call Single(string member) => Assert.Single(_buses.SelectMany(b => b.Calls), c => c.Member == member);
    private static Dictionary<string, object> Options(FakeBus.Call c) => (Dictionary<string, object>)c.Args[^1];

    // ---- the portal

    [Fact]
    public async Task TheMonitorIsAskedForOnceAndTheAnswerIsRememberedForNextTime()
    {
        var w = Wayland(Store());
        var (state, stream) = await Settle(w);
        Assert.Equal("live", state);
        Assert.Equal(new WaylandScreen.ScreenStream(42, 1920, 1080, false, true, "m1"), stream);
        var create = Single("CreateSession");
        Assert.Equal(WaylandScreen.ScreenCastIface, create.Iface); // no pointer asked for while the widget can't click
        var sources = Options(Single("SelectSources"));
        Assert.Equal(WaylandScreen.SourceMonitor, sources["types"]);
        Assert.Equal(WaylandScreen.PersistUntilRevoked, sources["persist_mode"]);
        Assert.False(sources.ContainsKey("restore_token"));
        Assert.Contains("tok-1", File.ReadAllText(StatePath));

        // the capture stops, and later starts again: with the token, so the portal doesn't ask again
        w.Idle();
        Assert.Equal("live", (await Settle(w)).State);
        var second = _buses.SelectMany(b => b.Calls).Where(c => c.Member == "SelectSources").Last();
        Assert.Equal("tok-1", Options(second)["restore_token"]);
        // and a new agent starts with it too
        var again = Wayland(Store());
        await Settle(again);
        Assert.Equal("tok-2", Options(_buses.SelectMany(b => b.Calls).Where(c => c.Member == "SelectSources").Last())["restore_token"]);
    }

    [Fact]
    public async Task ClickingAsksForTheRemoteDesktopSessionWithThePointerOnly()
    {
        var w = Wayland(Store(control: true));
        Assert.Equal("live", (await Settle(w)).State);
        Assert.Equal(WaylandScreen.RemoteDesktopIface, Single("CreateSession").Iface);
        var devices = Options(Single("SelectDevices"));
        Assert.Equal(WaylandScreen.DevicePointer, devices["types"]); // not the keyboard
        Assert.Equal(WaylandScreen.PersistUntilRevoked, devices["persist_mode"]);
        Assert.False(Options(Single("SelectSources")).ContainsKey("persist_mode")); // a remote desktop session persists through SelectDevices
        Assert.Equal(WaylandScreen.RemoteDesktopIface, Single("Start").Iface);
    }

    [Fact]
    public async Task TouchesBecomePortalPointerCallsOnTheSharedMonitor()
    {
        var w = Wayland(Store(control: true));
        await Settle(w);
        Assert.Equal((200, (string?)null), await w.PointerAsync("click", 0.5, 1, 0));
        await w.PointerAsync("scroll", 0, 0, 240);
        var calls = _buses.SelectMany(b => b.Calls).Where(c => c.Member.StartsWith("NotifyPointer", StringComparison.Ordinal)).ToList();
        Assert.Equal(["NotifyPointerMotionAbsolute", "NotifyPointerButton", "NotifyPointerButton", "NotifyPointerMotionAbsolute", "NotifyPointerAxisDiscrete"],
            calls.Select(c => c.Member));
        Assert.Equal(42u, calls[0].Args[2]);
        Assert.Equal(959.5, calls[0].Args[3]);
        Assert.Equal(1079.0, calls[0].Args[4]);
        Assert.Equal([WaylandScreen.ButtonLeft, 1u], calls[1].Args[2..]);
        Assert.Equal([WaylandScreen.ButtonLeft, 0u], calls[2].Args[2..]);
        Assert.Equal([0u, -2], calls[4].Args[2..]); // two notches away from you: up
    }

    [Fact]
    public async Task WithoutClickingAllowedThePointerIsRefused()
    {
        var w = Wayland(Store());
        await Settle(w);
        Assert.Equal(409, (await w.PointerAsync("click", 0.5, 0.5, 0)).Status);
        Assert.DoesNotContain(_buses.SelectMany(b => b.Calls), c => c.Member.StartsWith("NotifyPointer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusalIsRespectedUntilAllowedAgainInSettings()
    {
        var refuse = true;
        var w = Wayland(Store(), bus => { if (refuse) bus.Answers["Start"] = (1u, []); });
        Assert.Equal("denied", (await Settle(w)).State);
        Assert.Equal("denied", w.Ensure().State);
        Assert.Single(_buses); // no second dialog
        // also after a restart
        Assert.Equal("denied", Wayland(Store()).Ensure().State);
        refuse = false;
        w.Forget();
        Assert.Equal("live", (await Settle(w)).State);
    }

    [Fact]
    public async Task StoppingTheSharingOnTheComputerCountsAsARefusal()
    {
        var w = Wayland(Store());
        await Settle(w);
        var bus = _buses.Last();
        bus.Fire("/org/freedesktop/portal/desktop/session/1_42/s1", WaylandScreen.SessionIface, "Closed");
        Assert.Equal("denied", w.Ensure().State);
        Assert.Single(_buses);
    }

    [Fact]
    public async Task OtherPortalErrorsAreRetriedLaterNotAtOnce()
    {
        long now = 1_000_000;
        var w = Wayland(Store(), bus => bus.Answers["CreateSession"] = (2u, []));
        w.Now = () => now;
        Assert.Equal("failed", (await Settle(w)).State);
        Assert.Equal("failed", w.Ensure().State);
        Assert.Single(_buses);
        now += WaylandScreen.RetryMs;
        w.Ensure();
        await Settle(w);
        Assert.Equal(2, _buses.Count);
    }

    [Fact]
    public async Task AVirtualMonitorGetsThePhonesShapeAndClicksThroughLibei()
    {
        var w = Wayland(Store(control: true), bus => bus.Answers["Start"] = (0u, new()
        {
            ["streams"] = new List<object> { new object[] { 77u, new Dictionary<object, object> { ["source_type"] = 4u, ["mapping_id"] = "virt" } } },
            ["devices"] = 2u, ["restore_token"] = "tok-v",
        }));
        Assert.Equal((200, (string?)null), w.SetVirtual(true, 1600, 721));
        var (state, stream) = await Settle(w);
        Assert.Equal("live", state);
        Assert.Equal(new WaylandScreen.ScreenStream(77, 1600, 720, true, false, "virt"), stream); // even sizes
        Assert.Equal(WaylandScreen.SourceVirtual, Options(Single("SelectSources"))["types"]);
        Assert.Contains("path=77", Assert.Single(_sized));
        Assert.Contains("video/x-raw,width=1600,height=720", _sized[0]);
        // the portal can't place a pointer on a stream without a size, so libei does, on the portal's socket
        Single("ConnectToEIS");
        Assert.Equal([(99, (string?)"virt")], _eis);
        Assert.Equal((200, (string?)null), await w.PointerAsync("rightclick", 0.25, 0.5, 0));
        Assert.Equal(["move 0.25 0.5", $"button {WaylandScreen.ButtonRight} True", $"button {WaylandScreen.ButtonRight} False"], _eisPointer.Log);
        // the virtual monitor outlives the capture
        w.Idle();
        Assert.Equal("live", w.Ensure().State);
        w.SetVirtual(false, null, null);
        Assert.True(_eisPointer.Disposed);
    }

    [Fact]
    public async Task NothingStartsOutsideWayland()
    {
        var w = Wayland(Store());
        w.IsWayland = () => false;
        Assert.Equal("unsupported", w.Ensure().State);
        Assert.Equal(501, w.SetVirtual(true, null, null).Status);
        Assert.False((await w.InfoAsync(default)).Portal);
        Assert.Empty(_buses);
    }

    [Fact]
    public async Task TheSettingsCardLearnsWhatThePortalOffers()
    {
        var w = Wayland(Store());
        var info = await w.InfoAsync(default);
        Assert.True(info.Portal && info.VirtualAvailable);
        Assert.Equal(("idle", false, false), (info.State, info.Remembered, info.Virtual));
        Assert.DoesNotContain(_buses.SelectMany(b => b.Calls), c => c.Member == "CreateSession"); // looking starts nothing
    }

    // ---- GStreamer

    [Fact]
    public void GStreamerReadsTheNodeAndWritesJpegsAtTheQualitysWidth()
    {
        var args = WaylandScreen.GstArgs(new WaylandScreen.ScreenStream(42, 2560, 1440, false), 8, null);
        Assert.Equal(["-q", "pipewiresrc", "path=42", "always-copy=true", "keepalive-time=1000", "!", "videoconvert", "!", "videoscale", "!",
            "video/x-raw,width=1280,height=720,pixel-aspect-ratio=1/1", "!", "videorate", "drop-only=true", "max-rate=8", "!", "jpegenc", "quality=75",
            "!", "fdsink", "fd=1"], args);
        // a virtual monitor's size is pinned before scaling, or the desktop would shrink the monitor to the picture
        var v = WaylandScreen.GstArgs(new WaylandScreen.ScreenStream(7, 1600, 720, true), 15, "low");
        Assert.Equal(["videoconvert", "!", "video/x-raw,width=1600,height=720", "!", "videoscale"], v[6..11]);
        Assert.Contains("video/x-raw,width=960,height=432,pixel-aspect-ratio=1/1", v);
        Assert.Equal((1280, 576), WaylandScreen.Scaled(1601, 721, 1280));
        Assert.Equal((800, 450), WaylandScreen.Scaled(800, 450, 1280));
    }

    [Fact]
    public async Task TheSecondScreenWidgetCapturesTheSharedStreamWithGStreamer()
    {
        var store = Store(control: true);
        var w = Wayland(store);
        var launched = new List<(string Tool, IReadOnlyList<string> Args)>();
        var screen = new ScreenShare(store, NullLogger<ScreenShare>.Instance, (tool, args, _) => { launched.Add((tool, args)); return new NoRun(); },
            null, () => "ffmpeg", () => [], () => "wayland", timer: false, wayland: w) { FindGst = () => "/usr/bin/gst-launch-1.0" };
        Assert.Equal("starting", screen.Frame(null, "p/s").State);
        await Settle(w);
        Assert.Equal("starting", screen.Frame(null, "p/s").State);
        var (tool, args) = Assert.Single(launched);
        Assert.Equal("/usr/bin/gst-launch-1.0", tool);
        Assert.Contains("path=42", args);
        Assert.Equal(200, screen.Pointer(null, "p/s", "click", 0.1, 0.1, null).Status);

        var noGst = new ScreenShare(store, NullLogger<ScreenShare>.Instance, (_, _, _) => new NoRun(), null, () => "ffmpeg", () => [], () => "wayland",
            timer: false, wayland: w) { FindGst = () => null };
        Assert.Equal("no-gstreamer", noGst.Frame(null, "p/s").State);
    }

    private sealed class NoRun : ICameraRun
    {
        public bool HasEnded => false;
        public string? LastError => null;
        public void Dispose() { }
    }

    private sealed class FakePointer : IScreenPointer
    {
        public readonly List<string> Log = [];
        public bool Disposed;
        public Task Move(double x, double y) { Log.Add(FormattableString.Invariant($"move {x} {y}")); return Task.CompletedTask; }
        public Task Button(int button, bool down) { Log.Add($"button {button} {down}"); return Task.CompletedTask; }
        public Task Scroll(int steps) { Log.Add($"scroll {steps}"); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }

    // ---- the wire format

    [Fact]
    public void MessagesSurviveTheWireFormat()
    {
        var options = new Dictionary<string, object> { ["types"] = 4u, ["multiple"] = false, ["handle_token"] = "t1", ["size"] = new object[] { 1600, 720 } };
        var bytes = DBusWire.Encode(DBusWire.MethodCall, 0, 7, WaylandScreen.PortalName, "/s/1", WaylandScreen.RemoteDesktopIface,
            "NotifyPointerMotionAbsolute", 0, "oa{sv}udd", [new DBusPath("/s/1"), options, 42u, 1.5, 2.25]);
        Assert.Equal(bytes.Length, DBusWire.MessageLength(bytes.AsSpan(0, 16)));
        var m = DBusWire.Decode(bytes);
        Assert.Equal(("/s/1", WaylandScreen.RemoteDesktopIface, "NotifyPointerMotionAbsolute", "oa{sv}udd", 7u),
            (m.Path, m.Interface, m.Member, m.Signature, m.Serial));
        Assert.Equal(new DBusPath("/s/1"), m.Body[0]);
        var dict = (Dictionary<object, object>)m.Body[1];
        Assert.Equal(4u, dict["types"]);
        Assert.Equal(false, dict["multiple"]);
        Assert.Equal("t1", dict["handle_token"]);
        Assert.Equal([1600, 720], (object[])dict["size"]);
        Assert.Equal([42u, 1.5, 2.25], m.Body[2..]);
    }

    [Fact]
    public void APortalResponseWithStreamsIsRead()
    {
        // what Start answers: u response, a{sv} results with a(ua{sv}) streams
        var streams = new DBusVariant("a(ua{sv})", new List<object> { new object[] { 83u, new Dictionary<string, object> { ["id"] = "0", ["size"] = new object[] { 800, 600 } } } });
        var results = new Dictionary<string, object> { ["streams"] = streams, ["devices"] = 2u, ["restore_token"] = "abc", ["empty"] = new DBusVariant("as", new List<object>()) };
        var bytes = DBusWire.Encode(DBusWire.Signal, 0, 9, null, "/req/1", WaylandScreen.RequestIface, "Response", 0, "ua{sv}", [0u, results]);
        var m = DBusWire.Decode(bytes);
        var parsed = WaylandScreen.ParseStreams((Dictionary<object, object>)m.Body[1], false, 0, 0);
        Assert.Equal([new WaylandScreen.ScreenStream(83, 800, 600, false, true, null)], parsed);
        Assert.Empty((List<object>)((Dictionary<object, object>)m.Body[1])["empty"]);
    }

    [Fact]
    public void ArraysArePaddedToTheirElementsEvenWhenEmpty()
    {
        var w = new DBusWire.Writer();
        w.Value("y", (byte)1);
        w.Value("at", new List<object>());
        // byte, 3 bytes padding, length 0, then padding to 8 for the (absent) 64-bit elements
        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 0], w.ToArray());
        Assert.Equal(["u", "a{sv}", "(ii)", "a(ua{sv})"], DBusWire.SplitTypes("ua{sv}(ii)a(ua{sv})"));
    }

    [Theory]
    [InlineData("unix:path=/run/user/1000/bus", "/run/user/1000/bus")]
    [InlineData("unix:abstract=/tmp/dbus-x,guid=12ab", "\0/tmp/dbus-x")]
    [InlineData("tcp:host=localhost,port=1;unix:path=/a%20b", "/a b")]
    [InlineData("tcp:host=localhost,port=1", null)]
    [InlineData(null, null)]
    public void BusAddressesAreRead(string? address, string? expected) =>
        Assert.Equal(expected is null ? null : new System.Net.Sockets.UnixDomainSocketEndPoint(expected).ToString(), DBusWire.ParseAddress(address)?.ToString());
}

/// <summary>A session bus with a portal that answers every request at once, as scripted.</summary>
internal sealed class FakeBus(Func<string> nextToken) : IDBus
{
    public sealed record Call(string Path, string Iface, string Member, object[] Args);

    public readonly List<Call> Calls = [];
    public readonly Dictionary<string, (uint Code, Dictionary<object, object> Results)> Answers = [];
    private readonly List<(string Path, string Iface, string Member, Action<object[]> Handler)> _handlers = [];

    public string UniqueName => ":1.42";
    public bool Connected { get; private set; } = true;
    public event Action? Disconnected;

    private static readonly HashSet<string> Requests = ["CreateSession", "SelectDevices", "SelectSources", "Start"];

    public Task<object[]> CallAsync(string destination, string path, string iface, string member, string signature, object[] args, CancellationToken ct)
    {
        lock (Calls) Calls.Add(new Call(path, iface, member, args));
        if (member == "Get") return Task.FromResult<object[]>([7u]);
        if (member == "ConnectToEIS") return Task.FromResult<object[]>([new DBusFd(99)]);
        if (!Requests.Contains(member)) return Task.FromResult<object[]>([]);
        var handle = (string)((Dictionary<string, object>)args[^1])["handle_token"];
        var request = $"/org/freedesktop/portal/desktop/request/1_42/{handle}";
        var (code, results) = Answers.TryGetValue(member, out var a) ? a : (0u, Default(member));
        _ = Task.Run(() => Fire(request, WaylandScreen.RequestIface, "Response", code, results));
        return Task.FromResult<object[]>([new DBusPath(request)]);
    }

    private Dictionary<object, object> Default(string member) => member switch
    {
        "CreateSession" => new() { ["session_handle"] = "/org/freedesktop/portal/desktop/session/1_42/s1" },
        "Start" => new()
        {
            ["streams"] = new List<object> { new object[] { 42u, new Dictionary<object, object> { ["size"] = new object[] { 1920, 1080 }, ["mapping_id"] = "m1" } } },
            ["devices"] = 2u,
            ["restore_token"] = nextToken(),
        },
        _ => [],
    };

    public void Fire(string path, string iface, string member, params object[] body)
    {
        List<Action<object[]>> matched;
        lock (_handlers) matched = _handlers.Where(h => h.Path == path && h.Iface == iface && h.Member == member).Select(h => h.Handler).ToList();
        foreach (var h in matched) h(body);
    }

    public Task<IDisposable> SubscribeAsync(string path, string iface, string member, Action<object[]> handler, CancellationToken ct)
    {
        var entry = (path, iface, member, handler);
        lock (_handlers) _handlers.Add(entry);
        return Task.FromResult<IDisposable>(new Off(() => { lock (_handlers) _handlers.Remove(entry); }));
    }

    private sealed class Off(Action a) : IDisposable { public void Dispose() => a(); }

    public void Dispose()
    {
        if (!Connected) return;
        Connected = false;
        Disconnected?.Invoke();
    }
}
