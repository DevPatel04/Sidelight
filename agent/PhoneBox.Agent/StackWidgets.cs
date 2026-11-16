using System.Text.Json;

namespace PhoneBox.Agent;

internal static class StackWidgets
{
    internal sealed record Child(string Id, string Type, bool Enabled, Dictionary<string, JsonElement>? Options);
    public static IReadOnlyList<Child> Children(WidgetConfig parent)
    {
        if (parent.Type != "stack" || parent.Options is null || !parent.Options.TryGetValue("widgets", out var raw)) return [];
        try
        {
            if (raw.ValueKind == JsonValueKind.String) raw = JsonSerializer.Deserialize<JsonElement>(raw.GetString() ?? "[]");
            if (raw.ValueKind != JsonValueKind.Array) return [];
            return raw.EnumerateArray().Take(12).Where(x => x.ValueKind == JsonValueKind.Object
                && x.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is not ("stack" or "command") && Catalog.IsWidget(type.GetString()!))
                .Select((x, index) => new Child(x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()! : "card-" + index, x.GetProperty("type").GetString()!,
                    (!x.TryGetProperty("enabled", out var on) || on.ValueKind != JsonValueKind.False)
                        && (!x.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.False),
                    x.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options) : null
                )).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static readonly HashSet<string> ConsoleKeypadOptions = ["mode", "profileId", "unmatched", "showHeader", "showHints", "control"];
    private static readonly HashSet<string> ConsoleDialOptions = ["mode", "profileId", "unmatched", "axis", "showPills", "control"];
    private static readonly HashSet<string> ConsoleRollerOptions = ["mode", "profileId", "unmatched", "axis", "orientation", "showReset", "control"];

    public static Dictionary<string, JsonElement>? PublicOptions(WidgetConfig w) => PublicOptions(w.Type, w.Options);

    private static Dictionary<string, JsonElement>? PublicOptions(string? type, Dictionary<string, JsonElement>? source)
    {
        if (type == "command") return source?.Where(o => o.Key is "title" or "interval").ToDictionary();
        if (type == "app-keypad") return source?.Where(o => ConsoleKeypadOptions.Contains(o.Key)).ToDictionary();
        if (type == "app-dial") return source?.Where(o => ConsoleDialOptions.Contains(o.Key)).ToDictionary();
        if (type == "app-roller") return source?.Where(o => ConsoleRollerOptions.Contains(o.Key)).ToDictionary();
        if (type != "stack" || source is null) return source;
        var options = source.ToDictionary();
        if (options.ContainsKey("widgets")) options["widgets"] = JsonSerializer.SerializeToElement(Children(new WidgetConfig { Type = "stack", Options = source })
            .Select(child => new { id = child.Id, type = child.Type, enabled = child.Enabled, options = PublicOptions(child.Type, child.Options) }));
        return options;
    }
}
