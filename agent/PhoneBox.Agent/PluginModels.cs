using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? MinAgentVersion { get; set; }
    public List<string> Os { get; set; } = [];
    public List<string> Permissions { get; set; } = [];
    public List<PluginOptionDef> Settings { get; set; } = [];
    public PluginBackendDef Backend { get; set; } = new();
    public List<PluginWidgetDef> Widgets { get; set; } = [];
}

public sealed class PluginOptionDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public bool Secret { get; set; }
    public JsonElement? Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string[][]? Choices { get; set; }
    public bool IsSecret => Secret || Type == "password";
}

public sealed class PluginBackendDef { public string Kind { get; set; } = "none"; public string Runtime { get; set; } = "none"; }

public sealed class PluginWidgetDef
{
    public string Type { get; set; } = "";
    public string Category { get; set; } = "Plugins";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🧩";
    public string Desc { get; set; } = "";
    public int W { get; set; } = 4;
    public int H { get; set; } = 3;
    public int MinW { get; set; } = 2;
    public int MinH { get; set; } = 2;
    public PluginUiDef Ui { get; set; } = new();
    public List<PluginOptionDef> Options { get; set; } = [];
}

public sealed class PluginUiDef { public string Kind { get; set; } = "auto"; public string? File { get; set; } public JsonElement? Data { get; set; } }
public sealed class PluginsStateFile { public Dictionary<string, StoredPluginState> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase); }

public sealed class StoredPluginState
{
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";
    public string Source { get; set; } = "local";
    public string? Sha256 { get; set; }
    public bool NeedsReapproval { get; set; }
    public string? ChangeSummary { get; set; }
    public List<string> ApprovedPermissions { get; set; } = [];
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PluginInstallPathRequest(string? Path);
public sealed record PluginToggleRequest(bool Enabled, List<string>? ApprovedPermissions = null, bool ApproveChanges = false);
public sealed record PluginSettingsRequest(Dictionary<string, JsonElement>? Settings);

public static partial class PluginManifestValidator
{
    public const int MaxHtmlBytes = 512 * 1024;
    public const string LocalOnlyRejectedMessage = "HTTP, exec, and bridge plugins are not supported in local-only mode.";

    private static readonly HashSet<string> AllowedOs = ["windows", "linux", "macos"];
    private static readonly HashSet<string> UnsupportedLocalPermissions = new(["http", "exec", "bridge"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsupportedLocalBackends = new(["http", "command", "sidecar"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnsupportedLocalRuntimes = new(["shell", "python3", "node", "pwsh", "binary"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedOptionTypes = ["text", "password", "number", "bool", "select", "lines", "color", "textarea"];
    private static readonly HashSet<string> AllowedUiKinds = ["auto", "stat", "gauge", "list", "buttons", "html"];

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdRegex().IsMatch(id);

    public static bool IsValidVersion(string? ver, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(ver)) return false;
        return Version.TryParse(ver.Trim().TrimStart('v', 'V').Split('-')[0], out var v) && v is not null && (parsed = v) is not null;
    }

    public static bool IsCompatibleAgentVersion(string? minAgentVersion, string? currentAgentVersion = null)
    {
        if (string.IsNullOrWhiteSpace(minAgentVersion)) return true;
        if (!IsValidVersion(minAgentVersion, out var minV)) return false;
        return !IsValidVersion(currentAgentVersion ?? AppPaths.Version, out var curV) || curV >= minV;
    }

    public static bool IsSymlinkOrReparsePoint(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    public static string? ResolveSafeChildPath(string pluginDir, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.IndexOf('\0') >= 0) return null;
        var rel = relativePath.Trim();
        if (Path.IsPathRooted(rel) || rel.StartsWith('/') || rel.StartsWith('\\') || rel.Contains(':')) return null;
        var baseFull = Path.GetFullPath(pluginDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(baseFull, rel));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(baseFull + Path.DirectorySeparatorChar, cmp)) return null;
        if (Directory.Exists(baseFull) && IsSymlinkOrReparsePoint(new DirectoryInfo(baseFull))) return null;

        var cur = baseFull;
        foreach (var seg in Path.GetRelativePath(baseFull, target).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg is "." or "..") return null;
            cur = Path.Combine(cur, seg);
            if ((Directory.Exists(cur) && IsSymlinkOrReparsePoint(new DirectoryInfo(cur)))
                || (File.Exists(cur) && IsSymlinkOrReparsePoint(new FileInfo(cur))))
                return null;
        }
        return target;
    }

    public static string? Validate(PluginManifest? m, string? pluginDir)
    {
        if (m is null) return "plugin.json is empty";
        if (!IsValidId(m.Id)) return "plugin id must match ^[a-z0-9][a-z0-9-]{0,47}$";
        if (string.IsNullOrWhiteSpace(m.Name)) return "plugin name is required";
        if (!IsValidVersion(m.Version, out _)) return "plugin version must be a valid version (e.g. 1.0.0)";
        if (!string.IsNullOrWhiteSpace(m.MinAgentVersion) && !IsValidVersion(m.MinAgentVersion, out _))
            return "minAgentVersion must be a valid version";
        if (m.Os is not null && m.Os.Any(o => o is null || !AllowedOs.Contains(o)))
            return "unsupported os entry in plugin.json";

        m.Permissions ??= [];
        if (m.Permissions.Any(string.IsNullOrWhiteSpace)) return "unknown permission requested in plugin.json";
        if (m.Permissions.Any(UnsupportedLocalPermissions.Contains)) return LocalOnlyRejectedMessage;
        if (m.Permissions.Count > 0) return "unknown permission requested in plugin.json";

        m.Backend ??= new();
        var kind = (m.Backend.Kind ?? "none").Trim();
        var runtime = (m.Backend.Runtime ?? "none").Trim();
        if (UnsupportedLocalBackends.Contains(kind) || UnsupportedLocalRuntimes.Contains(runtime)) return LocalOnlyRejectedMessage;
        if (!string.Equals(kind, "none", StringComparison.OrdinalIgnoreCase)) return "unsupported backend kind";
        if (!string.Equals(runtime, "none", StringComparison.OrdinalIgnoreCase)) return "unsupported backend runtime";

        m.Settings ??= [];
        if (ValidateOptions(m.Settings, "plugin setting") is { } optErr) return optErr;
        if (m.Widgets is null || m.Widgets.Count == 0) return "plugin must declare at least one widget";
        if (m.Widgets.Count > 24) return "plugin cannot declare more than 24 widgets";

        var widgetTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in m.Widgets)
        {
            if (w is null) return "null widget entry";
            if (!Catalog.IsPluginWidgetType(w.Type) || Catalog.PluginIdOf(w.Type) != m.Id)
                return $"widget type '{w.Type}' must be namespaced as plugin:{m.Id} or plugin:{m.Id}.<name>";
            if (!widgetTypes.Add(w.Type)) return $"duplicate widget type '{w.Type}'";
            if (string.IsNullOrWhiteSpace(w.Name)) return $"widget '{w.Type}' requires a name";
            w.Category = string.IsNullOrWhiteSpace(w.Category) ? "Plugins" : w.Category.Trim();
            w.Icon = string.IsNullOrWhiteSpace(w.Icon) ? "🧩" : IconText.Repair(w.Icon);
            w.MinW = Math.Clamp(w.MinW <= 0 ? 1 : w.MinW, 1, Catalog.Cols);
            w.MinH = Math.Clamp(w.MinH <= 0 ? 1 : w.MinH, 1, Catalog.Rows);
            w.W = Math.Clamp(w.W <= 0 ? Math.Max(4, w.MinW) : w.W, w.MinW, Catalog.Cols);
            w.H = Math.Clamp(w.H <= 0 ? Math.Max(3, w.MinH) : w.H, w.MinH, Catalog.Rows);
            w.Ui ??= new();
            if (!AllowedUiKinds.Contains(w.Ui.Kind)) return $"unsupported ui kind '{w.Ui.Kind}'";
            if (w.Ui.Data is { } d && d.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
                return $"widget '{w.Type}' declarative ui.data must be a JSON object";
            if (w.Ui.Kind == "html")
            {
                if (string.IsNullOrWhiteSpace(w.Ui.File)) return $"widget '{w.Type}' html ui requires file";
                if (pluginDir is not null)
                {
                    var htmlPath = ResolveSafeChildPath(pluginDir, w.Ui.File);
                    if (htmlPath is null || !File.Exists(htmlPath)) return $"widget '{w.Type}' html file is missing or unsafe";
                    if (new FileInfo(htmlPath).Length > MaxHtmlBytes) return $"widget '{w.Type}' html file exceeds 512 KB";
                }
                else if (Path.IsPathRooted(w.Ui.File) || w.Ui.File.Contains("..") || w.Ui.File.Contains(':'))
                    return $"widget '{w.Type}' html file path must stay inside the plugin folder";
            }
            w.Options ??= [];
            if (ValidateOptions(w.Options, $"widget '{w.Type}' option") is { } wOptErr) return wOptErr;
        }
        return null;
    }

    private static string? ValidateOptions(List<PluginOptionDef> opts, string label)
    {
        if (opts.Any(o => o is null || string.IsNullOrWhiteSpace(o.Key) || !AllowedOptionTypes.Contains(o.Type)))
            return $"invalid {label} definition";
        if (opts.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != opts.Count)
            return $"duplicate {label} key";
        return null;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex IdRegex();
}
