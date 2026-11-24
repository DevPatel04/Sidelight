using System.Runtime.InteropServices;

namespace Sidelight.Agent;

/// <summary>Where the Second screen's touches go on Wayland: the portal's Notify* calls, or libei (<see cref="EisPointer"/>).</summary>
internal interface IScreenPointer : IDisposable
{
    /// <summary>Puts the pointer at <paramref name="x"/>, <paramref name="y"/> (0..1 across the shared monitor).</summary>
    Task Move(double x, double y);
    Task Button(int button, bool down);
    /// <summary>Notches of the wheel, positive toward you (down), as the portal and libei count them.</summary>
    Task Scroll(int steps);
}

/// <summary>
/// The pointer through libei, on the socket the RemoteDesktop portal's ConnectToEIS gives for our session. Used where the
/// portal's own NotifyPointerMotionAbsolute can't place the pointer: xdg-desktop-portal checks positions against the
/// stream size from Start, and GNOME's virtual monitor has none (its size is set later, by GStreamer). libei's absolute
/// device has one region per shared stream, found by the stream's mapping id, in desktop coordinates. libei isn't
/// thread-safe, so every call holds the lock; a thread of its own answers the compositor (pings, device changes).
/// </summary>
internal sealed class EisPointer : IScreenPointer
{
    private const string Lib = "libei.so.1";
    private const int CapPointerAbsolute = 1 << 1, CapScroll = 1 << 4, CapButton = 1 << 5;
    private const int EventDisconnect = 2, EventSeatAdded = 3, EventDeviceAdded = 5, EventDeviceRemoved = 6, EventDevicePaused = 7, EventDeviceResumed = 8;

    [DllImport(Lib)] private static extern nint ei_new_sender(nint userData);
    [DllImport(Lib)] private static extern nint ei_unref(nint ei);
    [DllImport(Lib)] private static extern void ei_configure_name(nint ei, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] private static extern int ei_setup_backend_fd(nint ei, int fd);
    [DllImport(Lib)] private static extern int ei_get_fd(nint ei);
    [DllImport(Lib)] private static extern void ei_dispatch(nint ei);
    [DllImport(Lib)] private static extern nint ei_get_event(nint ei);
    [DllImport(Lib)] private static extern ulong ei_now(nint ei);
    [DllImport(Lib)] private static extern int ei_event_get_type(nint ev);
    [DllImport(Lib)] private static extern nint ei_event_get_seat(nint ev);
    [DllImport(Lib)] private static extern nint ei_event_get_device(nint ev);
    [DllImport(Lib)] private static extern nint ei_event_unref(nint ev);
    // variadic in C (a NULL-terminated list of capabilities); with integer arguments only, the SysV and AAPCS64 calling
    // conventions pass them exactly like a fixed signature
    [DllImport(Lib)] private static extern void ei_seat_bind_capabilities(nint seat, int cap1, int cap2, int cap3, nint end);
    [DllImport(Lib)] private static extern nint ei_device_ref(nint device);
    [DllImport(Lib)] private static extern nint ei_device_unref(nint device);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool ei_device_has_capability(nint device, int cap);
    [DllImport(Lib)] private static extern nint ei_device_get_region(nint device, nuint index);
    [DllImport(Lib)] private static extern uint ei_region_get_x(nint region);
    [DllImport(Lib)] private static extern uint ei_region_get_y(nint region);
    [DllImport(Lib)] private static extern uint ei_region_get_width(nint region);
    [DllImport(Lib)] private static extern uint ei_region_get_height(nint region);
    [DllImport(Lib)] private static extern nint ei_region_get_mapping_id(nint region);
    [DllImport(Lib)] private static extern void ei_device_start_emulating(nint device, uint sequence);
    [DllImport(Lib)] private static extern void ei_device_pointer_motion_absolute(nint device, double x, double y);
    [DllImport(Lib)] private static extern void ei_device_button_button(nint device, uint button, [MarshalAs(UnmanagedType.U1)] bool down);
    [DllImport(Lib)] private static extern void ei_device_scroll_discrete(nint device, int x, int y);
    [DllImport(Lib)] private static extern void ei_device_frame(nint device, ulong time);

    private readonly Lock _gate = new();
    private readonly string? _mappingId;
    private readonly CancellationTokenSource _stop = new();
    private nint _ei;
    private nint _device;
    private bool _emulating;
    private uint _sequence;
    private (double X, double Y, double W, double H)? _region;

    private EisPointer(nint ei, string? mappingId)
    {
        _ei = ei;
        _mappingId = mappingId;
    }

    /// <summary>Whether libei can be loaded here (libei1; GNOME 46 and later use it).</summary>
    internal static bool Available() => NativeLibrary.TryLoad(Lib, out var h) && Free(h);

    private static bool Free(nint handle) { NativeLibrary.Free(handle); return true; }

    /// <summary>Takes over <paramref name="fd"/> (closed with this object) and waits up to two seconds for the device.</summary>
    internal static EisPointer? Start(int fd, string? mappingId)
    {
        if (!Available()) { DBusConnection.Close(fd); return null; }
        var ei = ei_new_sender(0);
        if (ei == 0) { DBusConnection.Close(fd); return null; }
        ei_configure_name(ei, "Sidelight second screen");
        if (ei_setup_backend_fd(ei, fd) != 0) { ei_unref(ei); DBusConnection.Close(fd); return null; } // fails before taking it over
        var p = new EisPointer(ei, mappingId);
        new Thread(p.Loop) { IsBackground = true, Name = "libei" }.Start();
        var until = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < until && !p.Ready) Thread.Sleep(20);
        return p;
    }

    internal bool Ready { get { lock (_gate) return _ei != 0 && _emulating && _region is not null; } }

    private void Loop()
    {
        int fd;
        lock (_gate) fd = _ei == 0 ? -1 : ei_get_fd(_ei);
        while (!_stop.IsCancellationRequested && fd >= 0)
        {
            var pfd = new DBusConnection.PollFd { Fd = fd, Events = DBusConnection.PollIn };
            PollOnce(ref pfd);
            lock (_gate)
            {
                if (_ei == 0) return;
                ei_dispatch(_ei);
                nint ev;
                while ((ev = ei_get_event(_ei)) != 0)
                {
                    try { Handle(ev); }
                    finally { ei_event_unref(ev); }
                }
            }
        }
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int PollNative(ref DBusConnection.PollFd fds, nuint count, int timeout);
    private static void PollOnce(ref DBusConnection.PollFd pfd) => PollNative(ref pfd, 1, 250);

    private void Handle(nint ev)
    {
        switch (ei_event_get_type(ev))
        {
            case EventSeatAdded:
                ei_seat_bind_capabilities(ei_event_get_seat(ev), CapPointerAbsolute, CapButton, CapScroll, 0);
                break;
            case EventDeviceAdded:
                var d = ei_event_get_device(ev);
                if (_device == 0 && ei_device_has_capability(d, CapPointerAbsolute) && FindRegion(d) is { } region)
                {
                    _device = ei_device_ref(d);
                    _region = region;
                }
                break;
            case EventDeviceRemoved:
                if (ei_event_get_device(ev) == _device) { ei_device_unref(_device); _device = 0; _emulating = false; _region = null; }
                break;
            case EventDevicePaused:
                if (ei_event_get_device(ev) == _device) _emulating = false;
                break;
            case EventDeviceResumed:
                if (ei_event_get_device(ev) == _device) { ei_device_start_emulating(_device, ++_sequence); _emulating = true; }
                break;
            case EventDisconnect:
                _emulating = false;
                break;
        }
    }

    /// <summary>The region of the shared stream (by mapping id); without one, a device with a single region.</summary>
    private (double, double, double, double)? FindRegion(nint device)
    {
        var regions = new List<(double X, double Y, double W, double H, string? Id)>();
        for (nuint i = 0; i < 64; i++)
        {
            var r = ei_device_get_region(device, i);
            if (r == 0) break;
            regions.Add((ei_region_get_x(r), ei_region_get_y(r), ei_region_get_width(r), ei_region_get_height(r),
                Marshal.PtrToStringUTF8(ei_region_get_mapping_id(r))));
        }
        var match = regions.FirstOrDefault(r => _mappingId is { Length: > 0 } && r.Id == _mappingId);
        if (match.W > 0) return (match.X, match.Y, match.W, match.H);
        return regions.Count == 1 && regions[0].W > 0 ? (regions[0].X, regions[0].Y, regions[0].W, regions[0].H) : null;
    }

    private void Emit(Action<nint> action)
    {
        lock (_gate)
        {
            if (_ei == 0 || _device == 0 || !_emulating) throw new InvalidOperationException("the libei pointer isn't ready");
            action(_device);
            ei_device_frame(_device, ei_now(_ei));
            ei_dispatch(_ei); // sends what was queued
        }
    }

    public Task Move(double x, double y)
    {
        (double X, double Y, double W, double H) r;
        lock (_gate) r = _region ?? throw new InvalidOperationException("the libei pointer has no region");
        Emit(d => ei_device_pointer_motion_absolute(d, r.X + Math.Clamp(x, 0, 1) * (r.W - 1), r.Y + Math.Clamp(y, 0, 1) * (r.H - 1)));
        return Task.CompletedTask;
    }

    public Task Button(int button, bool down)
    {
        Emit(d => ei_device_button_button(d, (uint)button, down));
        return Task.CompletedTask;
    }

    // libei counts a notch as 120, positive down, like the portal's steps
    public Task Scroll(int steps)
    {
        Emit(d => ei_device_scroll_discrete(d, 0, steps * 120));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            if (_ei == 0) return;
            if (_device != 0) ei_device_unref(_device);
            _device = 0;
            ei_unref(_ei); // closes the socket
            _ei = 0;
        }
    }
}

/// <summary>The pointer through the RemoteDesktop portal's Notify* calls, in the stream's own logical pixels.</summary>
internal sealed class PortalPointer(IDBus bus, string session, WaylandScreen.ScreenStream stream) : IScreenPointer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly Dictionary<string, object> None = [];

    private Task Call(string member, string signature, params object[] args) =>
        bus.CallAsync(WaylandScreen.PortalName, WaylandScreen.PortalPath, WaylandScreen.RemoteDesktopIface, member, signature,
            [new DBusPath(session), None, .. args], CancellationToken.None).WaitAsync(Timeout);

    public Task Move(double x, double y) => Call("NotifyPointerMotionAbsolute", "oa{sv}udd", stream.Node,
        Math.Clamp(x, 0, 1) * (stream.Width - 1), Math.Clamp(y, 0, 1) * (stream.Height - 1));

    public Task Button(int button, bool down) => Call("NotifyPointerButton", "oa{sv}iu", button, down ? 1u : 0u);

    public Task Scroll(int steps) => Call("NotifyPointerAxisDiscrete", "oa{sv}ui", 0u, steps);

    public void Dispose() { } // the session's connection is closed by WaylandScreen
}
