using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>App console: keys and dial bindings for one app, chosen by the process name of the focused window.</summary>
public sealed class ConsoleConfig
{
    public List<ConsoleProfile> Profiles { get; set; } = [];
}

public sealed class ConsoleProfile
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Process names the profile is for, comma-separated ("photoshop, gimp-2.10"); ".exe" may be left out.</summary>
    public string Apps { get; set; } = "";
    public List<ConsoleKey> Keys { get; set; } = [];
    public string? DialLeft { get; set; }
    public string? DialRight { get; set; }
    public string? DialPress { get; set; }

    public const int MaxKeys = 12;

    public IEnumerable<string> AppNames() => Apps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).Where(a => a.Length > 0);

    public bool Matches(string process) => AppNames().Any(a => string.Equals(a, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>The key spec behind a slot ("k0".."k11", "dial-left", "dial-right", "dial-press"), or null.</summary>
    public string? Spec(string slot) => slot switch
    {
        "dial-left" => DialLeft,
        "dial-right" => DialRight,
        "dial-press" => DialPress,
        ['k', .. var n] when int.TryParse(n, System.Globalization.NumberStyles.None, null, out var i) && i < Keys.Count && n == i.ToString() => Keys[i].Keys,
        _ => null,
    };
}

public sealed class ConsoleKey
{
    public string Label { get; set; } = "";
    public string Keys { get; set; } = "";
}

/// <summary>The focused window's process name, and whether it runs elevated (Windows doesn't let us type into it then).</summary>
public sealed record FocusedApp(string Process, bool Elevated);

/// <summary>
/// Follows the focused app and sends a profile's keys to it. Off unless an enabled page of the phone shows an
/// app-console widget with "control" on; the phone sends a profile id and a slot, never keys; the focused app is
/// checked again right before every combo, so keys never land in an app the profile isn't for.
/// </summary>
public sealed class AppConsole(ConfigStore config, ILogger<AppConsole> log)
{
    /// <summary>Only Windows can say which app is focused without extra permissions or tools.</summary>
    internal bool Supported { get; set; } = OperatingSystem.IsWindows();

    internal Func<FocusedApp?> Focused { get; set; } = FocusedNow;
    internal Func<List<string>, bool> SendCombo { get; set; } = KeySender.SendCombo;
    internal bool AgentElevated { get; set; } = Environment.IsPrivilegedProcess;

    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>App console widgets on the enabled pages this phone shows (Smart Stack children included).</summary>
    public static IEnumerable<WidgetConfig> WidgetsFor(AgentConfig c, string? deviceId) => c.PagesFor(deviceId).Where(p => p.Enabled)
        .SelectMany(p => p.Widgets ?? []).SelectMany(w => new[] { w }.Concat(StackWidgets.Children(w).Where(ch => ch.Enabled)
            .Select(ch => new WidgetConfig { Type = ch.Type, Options = ch.Options })))
        .Where(w => w.Type == "app-console");

    /// <summary>What the phone may know: the matching profile's id, label and key labels. Never the process name or title.</summary>
    public (int Status, object Body) State(string? deviceId)
    {
        var c = config.Get();
        var widgets = WidgetsFor(c, deviceId).ToList();
        if (widgets.Count == 0) return (404, new { error = "no app console widget on this phone's pages" });
        var control = widgets.Any(w => w.Bool("control") == true);
        if (!Supported) return (200, new { supported = false, control, profile = (object?)null });
        var app = Focused();
        var profile = app is null ? null : c.Console.Profiles.FirstOrDefault(p => p.Matches(app.Process));
        return (200, new
        {
            supported = true,
            control,
            profile = profile is null ? null : new
            {
                profile.Id,
                profile.Label,
                keys = profile.Keys.Select(k => k.Label),
                dial = new { left = profile.DialLeft is not null, right = profile.DialRight is not null, press = profile.DialPress is not null },
            },
        });
    }

    /// <summary>Presses a slot of a profile. <paramref name="times"/> repeats a dial turn (clamped to 1..5).</summary>
    public async Task<(int Status, string? Error)> Act(string? deviceId, string profileId, string slot, int times, CancellationToken ct)
    {
        var c = config.Get();
        if (!WidgetsFor(c, deviceId).Any(w => w.Bool("control") == true))
            return (403, "sending keys is off: turn on \"Send keys to the computer\" in the app console widget's settings");
        if (!Supported) return (409, "the app console needs Windows");
        var profile = c.Console.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null) return (404, "no such profile");
        var spec = profile.Spec(slot);
        if (string.IsNullOrWhiteSpace(spec)) return (404, "nothing is set for this key");
        List<List<string>> combos;
        try { combos = KeySender.Parse(spec); }
        catch (ArgumentException ex) { return (400, ex.Message); }
        times = slot is "dial-left" or "dial-right" ? Math.Clamp(times, 1, 5) : 1;

        if (!await _sending.WaitAsync(TimeSpan.FromSeconds(2), ct)) return (429, "still sending the previous keys");
        try
        {
            for (var t = 0; t < times; t++)
                foreach (var combo in combos)
                {
                    // checked again before every combo: a click elsewhere mid-macro must not send the rest there
                    var app = Focused();
                    if (app is null || !profile.Matches(app.Process)) return (409, "the focused app changed");
                    if (app.Elevated && !AgentElevated) return (409, "the focused app runs as administrator, so Windows doesn't let the agent send keys to it");
                    if (!SendCombo(combo)) return (500, "Windows refused the key press");
                    if (combos.Count > 1 || times > 1) await Task.Delay(40, ct);
                }
            return (200, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("app console {Profile}/{Slot}: {Msg}", profileId, slot, ex.Message);
            return (500, ex.Message);
        }
        finally { _sending.Release(); }
    }

    /// <summary>Checks a profile's key specs; null when they are all valid.</summary>
    public static string? Validate(ConsoleProfile p)
    {
        if (!p.AppNames().Any()) return $"App console profile '{p.Label}' has no app";
        foreach (var (spec, what) in p.Keys.Select((k, i) => ((string?)k.Keys, $"key {i + 1}")).Append((p.DialLeft, "dial left")).Append((p.DialRight, "dial right")).Append((p.DialPress, "dial press")))
        {
            if (spec is null) continue;
            try { KeySender.Parse(spec); }
            catch (ArgumentException ex) { return $"App console profile '{p.Label}', {what}: {ex.Message}"; }
        }
        return null;
    }

    private static FocusedApp? FocusedNow()
    {
#if WINDOWS
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return new FocusedApp(p.ProcessName, Elevated(pid));
        }
        catch (ArgumentException) { return null; } // gone
        catch (InvalidOperationException) { return null; }
#else
        return null;
#endif
    }

#if WINDOWS
    /// <summary>True when the process runs elevated, or when its token can't even be read (which is how an elevated one looks from here).</summary>
    private static bool Elevated(uint pid)
    {
        var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) return true;
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return true;
            try { return !GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, sizeof(int), out _) || elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
#endif
}
