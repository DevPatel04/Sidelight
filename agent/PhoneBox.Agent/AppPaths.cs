using System.Reflection;

namespace PhoneBox.Agent;

/// <summary>File locations. The install folder is replaced on update; settings and logs are kept.</summary>
public static class AppPaths
{
    public const string AppName = "PhoneBox";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Home, fallback);

    /// <summary>%APPDATA%\PhoneBox — user settings (config.json).</summary>
    /// <summary>Windows: %APPDATA%\PhoneBox. Linux: ~/.config/PhoneBox. macOS: ~/Library/Application Support/PhoneBox.</summary>
    public static readonly string ConfigDir = Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_CONFIG_HOME", ".config"), AppName);

    /// <summary>%LOCALAPPDATA%\PhoneBox — logs and the installed binaries.</summary>
    /// <summary>Windows: %LOCALAPPDATA%\PhoneBox. Linux: ~/.local/share/PhoneBox. macOS: same folder as the settings.</summary>
    public static readonly string LocalDir = Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support")
        : Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), AppName);

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public static readonly string LogPath = Path.Combine(LocalDir, "agent.log");

    /// <summary>Where install scripts put the agent; self-update only works for this copy.</summary>
    public static readonly string InstallDir = Path.Combine(LocalDir, "app");

    /// <summary>"windows", "linux" or "macos".</summary>
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Release asset suffix for this machine: win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64.</summary>
    public static string Rid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    public static readonly string StartupShortcut =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Writes the default settings file on first run; tightens an existing one (owner only on macOS/Linux).</summary>
    public static void EnsureConfig()
    {
        if (File.Exists(ConfigPath))
        {
            // older versions wrote config.json with the umask (often 0644); it holds the Wi-Fi and webhook keys
            RestrictToOwner(ConfigPath);
            RestrictToOwner(ConfigPath + ".bak");
            return;
        }
        Directory.CreateDirectory(ConfigDir);
        var name = OperatingSystem.IsMacOS() ? "config.default.macos.json" : OperatingSystem.IsLinux() ? "config.default.linux.json" : "config.default.json";
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var dst = CreatePrivate(ConfigPath);
        src.CopyTo(dst);
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates (or truncates) a file only its owner can read on macOS/Linux (0600); Windows keeps the profile ACLs.</summary>
    public static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        var stream = new FileStream(path, options);
        RestrictToOwner(path); // UnixCreateMode only applies when the file is new
        return stream;
    }

    /// <summary><see cref="File.WriteAllText(string, string?)"/> into a file only its owner can read (see <see cref="CreatePrivate"/>).</summary>
    public static void WriteAllTextPrivate(string path, string text)
    {
        using var stream = CreatePrivate(path);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>Sets an existing file to 0600 on macOS/Linux; no-op on Windows or when the file is missing.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try { if (File.GetUnixFileMode(path) != OwnerOnly) File.SetUnixFileMode(path, OwnerOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static Stream? Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    /// <summary>Neutral working directory for child processes, so they never lock the install folder.</summary>
    public static string NeutralCwd => Path.GetTempPath();
}
