using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Application-aware Console configuration: profiles, key sub-pages, and rotary/roller axes.</summary>
public sealed class AppConsoleConfig
{
    public bool Enabled { get; set; } = false;
    public bool AutoFollow { get; set; } = true;
    public bool FocusBeforeKeys { get; set; } = true;
    public List<ConsoleProfile>? Profiles { get; set; }
}

public sealed class ConsoleProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "🎛️";
    public string Color { get; set; } = "#38bdf8";
    public string? AppMatch { get; set; }
    public string? TitleMatch { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 2;
    public List<ConsoleSubPage>? SubPages { get; set; }
    public List<ConsoleAxis>? Axes { get; set; }
}

public sealed class ConsoleSubPage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Main";
    public List<ConsoleSlot>? Slots { get; set; }
}

public sealed class ConsoleSlot
{
    public string Kind { get; set; } = "keys";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string? Hint { get; set; }
    public string? Color { get; set; }
    public bool Toggle { get; set; }
    public string? StateKey { get; set; }
    public string? Keys { get; set; }
    public List<string>? Steps { get; set; }
    public string? Text { get; set; }
    public string? Target { get; set; }
}

public sealed class ConsoleAxis
{
    public int Index { get; set; } = 1;
    public string Name { get; set; } = "Axis";
    public string Icon { get; set; } = "🎚️";
    public string Kind { get; set; } = "keys";
    public string? PrevKeys { get; set; }
    public string? NextKeys { get; set; }
    public string? TapAction { get; set; }
    public string? AppTarget { get; set; }
    public string Sensitivity { get; set; } = "normal";
}

public sealed record ConsoleWindowInfo(
    string Process,
    string Title,
    [property: JsonIgnore] nint Handle = 0,
    [property: JsonIgnore] int Pid = 0,
    [property: JsonIgnore] long StartTicks = 0);

public sealed record ConsoleSnapshot(
    bool AutoFollow,
    string? MatchedProfileId,
    string? PinnedProfileId,
    string? EffectiveProfileId,
    Dictionary<string, bool> SlotStates,
    long UpdatedAt);

public sealed record ConsoleActRequest(
    string Kind,
    string? ProfileId = null,
    string? SubPageId = null,
    int? Slot = null,
    int? Axis = null,
    int? Delta = null,
    string? Pinned = null);

public static partial class AppConsole
{
    private const string DefaultColor = "#38bdf8";

    public static readonly HashSet<string> WidgetTypes = ["app-keypad", "app-dial", "app-roller"];
    public static readonly HashSet<string> SlotKinds = ["empty", "keys", "macro", "text", "shortcut", "deck"];
    public static readonly HashSet<string> AxisKinds = ["keys", "system-volume", "app-volume", "mic-gain", "media-seek", "brightness"];
    public static readonly HashSet<string> Sensitivities = ["fine", "normal", "coarse"];

    public static bool IsWidget(string? type) => type is not null && WidgetTypes.Contains(type);

    public static bool HasWidget(AgentConfig cfg, string? deviceId) =>
        cfg.WidgetsFor(deviceId, enabledPagesOnly: true).Any(w => IsWidget(w.Type));

    public static bool ControlAllowed(AgentConfig cfg, string? deviceId) =>
        cfg.WidgetsFor(deviceId, enabledPagesOnly: true).Any(w => IsWidget(w.Type) && w.Bool("control") == true);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorRe();

    public static string? ValidColor(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && HexColorRe().IsMatch(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    private static string? Clean(string? value, int maxLen = 256)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > maxLen ? s[..maxLen] : s;
    }

    private static string CleanIcon(string? value, string fallback)
    {
        var repaired = IconText.Repair(value)?.Trim();
        return string.IsNullOrEmpty(repaired) ? fallback : (repaired.Length > 16 ? repaired[..16] : repaired);
    }

    private static string UniqueSlug(string? raw, string prefix, HashSet<string> used)
    {
        var id = ConfigStore.Slug(string.IsNullOrWhiteSpace(raw) ? "" : raw);
        if (id == "") id = prefix;
        var unique = id;
        for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
        return unique;
    }

    public static AppConsoleConfig Normalize(AppConsoleConfig? cfg)
    {
        cfg ??= new();
        var list = cfg.Profiles ?? [];
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var defaultSeen = false;
        var normalized = new List<ConsoleProfile>();

        foreach (var p in list.Where(x => x is not null).Take(48))
        {
            p.Name = Clean(p.Name, 48) ?? "Profile";
            p.Id = UniqueSlug(string.IsNullOrWhiteSpace(p.Id) ? p.Name : p.Id, "profile", usedIds);
            p.Icon = CleanIcon(p.Icon, p.IsDefault ? "🖥️" : "🎛️");
            p.Color = ValidColor(p.Color) ?? DefaultColor;
            p.AppMatch = Clean(p.AppMatch, 256);
            p.TitleMatch = Clean(p.TitleMatch, 256);
            p.Columns = Math.Clamp(p.Columns == 0 ? 4 : p.Columns, 2, 6);
            p.Rows = Math.Clamp(p.Rows == 0 ? 2 : p.Rows, 1, 4);

            if (p.IsDefault)
            {
                if (defaultSeen) p.IsDefault = false;
                else defaultSeen = true;
            }

            p.SubPages = NormalizeSubPages(p.SubPages);
            p.Axes = NormalizeAxes(p.Axes);
            normalized.Add(p);
        }

        cfg.Profiles = normalized;
        return cfg;
    }

    private static List<ConsoleSubPage> NormalizeSubPages(List<ConsoleSubPage>? pages)
    {
        var src = pages is { Count: > 0 } ? pages : [new ConsoleSubPage { Id = "main", Name = "Main", Slots = [] }];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ConsoleSubPage>();

        foreach (var sp in src.Where(x => x is not null).Take(16))
        {
            sp.Name = Clean(sp.Name, 32) ?? "Main";
            sp.Id = UniqueSlug(string.IsNullOrWhiteSpace(sp.Id) ? sp.Name : sp.Id, "page", used);
            sp.Slots = NormalizeSlots(sp.Slots);
            result.Add(sp);
        }

        if (result.Count == 0) result.Add(new ConsoleSubPage { Id = "main", Name = "Main", Slots = [] });
        return result;
    }

    private static List<ConsoleSlot> NormalizeSlots(List<ConsoleSlot>? slots)
    {
        var result = new List<ConsoleSlot>();
        foreach (var s in (slots ?? []).Take(24))
        {
            if (s is null) { result.Add(new ConsoleSlot { Kind = "empty" }); continue; }
            s.Kind = SlotKinds.Contains(s.Kind) ? s.Kind : "keys";
            s.Label = Clean(s.Label, 36) ?? (s.Kind == "empty" ? "" : "Key");
            s.Icon = CleanIcon(s.Icon, s.Kind == "empty" ? "" : "🔘");
            s.Hint = Clean(s.Hint, 20);
            s.Color = ValidColor(s.Color);
            s.StateKey = Clean(s.StateKey, 64);
            s.Keys = Clean(s.Keys, 128);
            s.Text = s.Text is { Length: > 0 } ? (s.Text.Length > 2000 ? s.Text[..2000] : s.Text) : null;
            s.Target = Clean(s.Target, 256);
            s.Steps = s.Steps?.Select(x => Clean(x, 200)).Where(x => x is not null).Select(x => x!).Take(32).ToList();
            if (s.Steps is { Count: 0 }) s.Steps = null;
            if (s.Kind == "empty")
            {
                s.Toggle = false;
                s.Keys = s.Text = s.Target = s.Hint = s.StateKey = null;
                s.Steps = null;
            }
            result.Add(s);
        }
        return result;
    }

    private static List<ConsoleAxis> NormalizeAxes(List<ConsoleAxis>? axes)
    {
        var seen = new HashSet<int>();
        var result = new List<ConsoleAxis>();
        foreach (var a in (axes ?? []).Where(x => x is not null).Take(16))
        {
            var idx = Math.Clamp(a.Index == 0 ? 1 : a.Index, 1, 8);
            if (!seen.Add(idx)) continue;
            a.Index = idx;
            a.Name = Clean(a.Name, 32) ?? $"Axis {idx}";
            a.Icon = CleanIcon(a.Icon, "🎚️");
            a.Kind = AxisKinds.Contains(a.Kind) ? a.Kind : "keys";
            a.Sensitivity = Sensitivities.Contains(a.Sensitivity) ? a.Sensitivity : "normal";
            a.PrevKeys = Clean(a.PrevKeys, 128);
            a.NextKeys = Clean(a.NextKeys, 128);
            a.TapAction = Clean(a.TapAction, 128);
            a.AppTarget = Clean(a.AppTarget, 128);
            result.Add(a);
        }
        return result.OrderBy(a => a.Index).ToList();
    }

    public static bool HasDamagedIcons(AppConsoleConfig? cfg) =>
        (cfg?.Profiles ?? []).Any(p =>
            p is not null && (IconText.Repair(p.Icon) != p.Icon
                || (p.Axes ?? []).Any(a => a is not null && IconText.Repair(a.Icon) != a.Icon)
                || (p.SubPages ?? []).Any(sp => sp is not null && (sp.Slots ?? []).Any(s => s is not null && IconText.Repair(s.Icon) != s.Icon))));

    /// <summary>Phone-safe projection: never exposes keystrokes, typed text, macros, targets, or match rules.</summary>
    public static object ToPublic(AppConsoleConfig? cfg)
    {
        var c = cfg ?? new();
        return new
        {
            enabled = c.Enabled,
            autoFollow = c.AutoFollow,
            profiles = c.Enabled
                ? (c.Profiles ?? []).Select(p => (object)new
                {
                    p.Id,
                    p.Name,
                    p.Icon,
                    p.Color,
                    p.IsDefault,
                    p.Enabled,
                    p.Columns,
                    p.Rows,
                    subPages = (p.SubPages ?? []).Select(sp => new
                    {
                        sp.Id,
                        sp.Name,
                        slots = (sp.Slots ?? []).Select(s => new
                        {
                            s.Kind,
                            s.Label,
                            s.Icon,
                            hint = s.Kind == "keys" && !string.IsNullOrWhiteSpace(s.Hint)
                                ? (s.Hint.Length > 20 ? s.Hint[..20] : s.Hint)
                                : null,
                            s.Color,
                            s.Toggle,
                            stateKey = s.Toggle ? s.StateKey : null,
                        }),
                    }),
                    axes = (p.Axes ?? []).Select(a => new
                    {
                        a.Index,
                        a.Name,
                        a.Icon,
                        a.Kind,
                        a.Sensitivity,
                        hasTap = !string.IsNullOrWhiteSpace(a.TapAction),
                    }),
                })
                : [],
        };
    }
}
