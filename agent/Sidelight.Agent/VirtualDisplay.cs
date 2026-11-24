using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sidelight.Agent;

/// <summary>
/// Settings › Sources › Second screen: what the Second screen widget needs on this computer, and on Windows a button
/// that installs the open-source Virtual Display Driver's control app (VDD Control) with winget and opens it. winget
/// checks the download against the hash in its community manifest; the driver itself is installed by the user pressing
/// Install in VDD Control, which asks Windows for administrator rights. On Wayland the desktop's screen-sharing portal
/// adds the virtual monitor instead (<see cref="WaylandScreen"/>), after asking on the computer. Nothing here runs
/// without a click on the settings page, and these endpoints are local only (not on the Wi-Fi allow-list).
/// </summary>
public sealed class VirtualDisplay(ConfigStore config, ILogger<VirtualDisplay> log, WaylandScreen? wayland = null)
{
    public const string WingetId = "VirtualDrivers.Virtual-Display-Driver";
    internal const string ControlExe = "VDD Control.exe";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    internal Func<string?> FindWinget { get; set; } = () => OperatingSystem.IsWindows() ? ToolProcess.Find("winget") : null;
    internal Func<string?> FindControl { get; set; } = FindControlApp;
    internal Func<bool> DriverPresent { get; set; } = VirtualAdapterPresent;
    internal Func<IReadOnlyList<ScreenMonitor>> Monitors { get; set; } = () => ScreenShare.ListMonitors();
    internal Func<string> PlatformName { get; set; } = CurrentPlatform;
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<int>> Run { get; set; } = RunTool;
    internal Func<string, bool> Launch { get; set; } = LaunchApp;
    /// <summary>The portal's answers for the Wayland part of the card; null elsewhere.</summary>
    internal Func<CancellationToken, Task<WaylandScreen.Info?>> WaylandInfo { get; set; } = async ct => wayland is null ? null : await wayland.InfoAsync(ct);
    /// <summary>Whether GStreamer with its PipeWire plugin (pipewiresrc) is installed, which the Wayland capture needs.</summary>
    internal Func<CancellationToken, Task<bool>> GstPipewire { get; set; } = HasPipewireSrc;

    private readonly SemaphoreSlim _installing = new(1, 1);

    public sealed record Status(string Platform, bool Capture, bool Ffmpeg, IReadOnlyList<ScreenMonitor> Monitors,
        bool Driver, bool ControlApp, bool Winget, bool Installing, bool Gstreamer = false, WaylandScreen.Info? Wayland = null);

    public Status Get()
    {
        IReadOnlyList<ScreenMonitor> monitors;
        var platform = PlatformName();
        // Wayland has no monitor list for other programs; the portal's dialog picks one
        try { monitors = platform == "wayland" ? [] : Monitors(); } catch { monitors = []; }
        var windows = platform == "windows";
        return new Status(platform, platform is "windows" or "x11", CameraFeeds.FindFfmpeg(config.Get().Cameras.Ffmpeg) is not null, monitors,
            windows && DriverPresent(), windows && FindControl() is not null, windows && FindWinget() is not null, _installing.CurrentCount == 0);
    }

    /// <summary><see cref="Get"/>, and on Wayland what the screen-sharing portal offers and whether GStreamer can read it.</summary>
    public async Task<Status> GetAsync(CancellationToken ct)
    {
        var st = Get();
        if (st.Platform != "wayland") return st;
        var info = await WaylandInfo(ct);
        var gst = await GstPipewire(ct);
        return st with { Capture = info?.Portal == true, Gstreamer = gst, Wayland = info };
    }

    private static async Task<bool> HasPipewireSrc(CancellationToken ct)
    {
        if (ToolProcess.Find("gst-inspect-1.0") is not { } inspect) return false;
        var psi = new ProcessStartInfo(inspect);
        psi.ArgumentList.Add("--exists");
        psi.ArgumentList.Add("pipewiresrc");
        try { return (await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(5), ct)).ExitCode == 0; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>Installs VDD Control with winget (unless it is there already), then opens it for the driver's Install button.</summary>
    public async Task<(int Status, string? Error)> InstallAsync(CancellationToken ct)
    {
        if (PlatformName() != "windows") return (501, "the virtual display driver is for Windows");
        if (FindControl() is null)
        {
            if (FindWinget() is not { } winget) return (412, "winget isn't available: install \"App Installer\" from the Microsoft Store, or follow docs/SECOND-SCREEN.md");
            if (!await _installing.WaitAsync(0, ct)) return (409, "already installing");
            try
            {
                log.LogInformation("second screen: installing {Id} with winget", WingetId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(InstallTimeout);
                // the source is named so a package of the same id from another source can't be picked; the agreements are
                // accepted because the user pressed the button that says it installs this package (MIT licence)
                var code = await Run(winget, ["install", "--id", WingetId, "--exact", "--source", "winget",
                    "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"], deadline.Token);
                if (FindControl() is null)
                {
                    log.LogWarning("second screen: winget ended with {Code} and VDD Control isn't there", code);
                    return (500, $"winget couldn't install it (exit code {code})");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (504, "the install took too long"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("second screen: winget failed ({Type})", ex.GetType().Name);
                return (500, "winget couldn't be started");
            }
            finally { _installing.Release(); }
        }
        return Open();
    }

    /// <summary>Opens VDD Control, where the driver is installed and monitors are added or removed.</summary>
    public (int Status, string? Error) Open()
    {
        if (PlatformName() != "windows") return (501, "the virtual display driver is for Windows");
        if (FindControl() is not { } exe) return (404, "VDD Control isn't installed");
        return Launch(exe) ? (200, null) : (500, "VDD Control couldn't be opened");
    }

    internal static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos"
        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "wayland"
        : ScreenShare.Platform() is not null ? "x11" : "linux";

    /// <summary>winget keeps a portable app under its Packages folder, in a folder named after the package id.</summary>
    private static string? FindControlApp()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        try
        {
            if (!Directory.Exists(packages)) return null;
            foreach (var dir in Directory.EnumerateDirectories(packages, WingetId + "*"))
                if (Directory.EnumerateFiles(dir, ControlExe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }).FirstOrDefault() is { } exe)
                    return exe;
        }
        catch (Exception) { }
        return null;
    }

    private static async Task<int> RunTool(string tool, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return (await ToolProcess.RunAsync(psi, InstallTimeout, ct)).ExitCode;
    }

    /// <summary>
    /// Through the Windows shell (no arguments, no command line): VDD Control asks for administrator rights, and only
    /// ShellExecute shows that prompt; a plain start fails with "elevation required".
    /// </summary>
    private static bool LaunchApp(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            return true;
        }
        catch (Exception) { return false; } // the user said no to the prompt, or it is gone
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    /// <summary>Whether a display adapter from a virtual display driver is there (VDD, or the older IddSampleDriver).</summary>
    private static bool VirtualAdapterPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        for (uint i = 0; i < 32; i++)
        {
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref d, 0)) break;
            if (IsVirtualAdapter(d.DeviceString)) return true;
        }
        return false;
    }

    internal static bool IsVirtualAdapter(string? name) =>
        name is not null && (name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase) || name.Contains("IddSampleDriver", StringComparison.OrdinalIgnoreCase));
}
