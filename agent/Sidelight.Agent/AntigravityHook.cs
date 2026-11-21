using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sidelight.Agent;

/// <summary>
/// Antigravity CLI (agy) integration: a PreToolUse hook in ~/.gemini/config/hooks.json posts tool calls that agy
/// normally asks about (commands, file writes, web access) to the agent, which shows them on the phone.
/// Allow becomes "auto_approve", Deny becomes "deny", and no answer becomes "allow" — which in agy only hands the
/// call on to its own policy, so it asks at the PC as usual.
/// </summary>
/// <remarks>
/// Unlike Claude Code's PermissionRequest, PreToolUse runs before agy decides whether to prompt at all, so the hook
/// is limited to the tools agy's default policy reviews, and calls your agy rules would allow still reach the phone.
/// An empty reply counts as a denial in agy, so every path (including the agent being down) must print a decision.
/// </remarks>
public static class AntigravityHook
{
    public const string Endpoint = "/api/hooks/antigravity/permission";
    private const string HookName = "sidelight";
    // the name this hook had before the fork was renamed; one agent answers both, so the old entry would ask twice
    private const string LegacyHookName = "phonebox";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".gemini", "config", "hooks.json");

    /// <summary>The tools agy reviews by default; reading and searching the workspace never prompts.</summary>
    internal const string Matcher = "^(run_command|write_to_file|replace_file_content|multi_replace_file_content|read_url_content|search_web|ask_question)$";

    internal static readonly object NoDecision = new { decision = "allow" };

    /// <summary>Handles one hook call; always returns a decision for the hook to print.</summary>
    public static async Task<object> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals, CancellationToken aborted)
    {
        var c = cfg.Get().Antigravity;
        if (!c.PhoneApprovals) return NoDecision;
        if (c.OnlyWhenAway && Idle.Seconds() < 60) return NoDecision;
        if (Describe(input, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), c.TimeoutSec) is not { } a) return NoDecision;
        return Output(await approvals.AskAsync(a, aborted));
    }

    /// <summary>The phone's answer as agy hook output.</summary>
    internal static object Output(string? decision) => decision switch
    {
        "allow" => new { decision = "auto_approve" },
        "deny" => new { decision = "deny", reason = "Denied from the Sidelight panel." },
        _ => NoDecision,
    };

    /// <summary>The request as the phone shows it, or null when the input is not a tool call.</summary>
    internal static Approval? Describe(JsonElement input, long now, int timeoutSec)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("toolCall", out var call) || call.ValueKind != JsonValueKind.Object) return null;
        var tool = ClaudeHook.Str(call, "name");
        if (string.IsNullOrEmpty(tool)) return null;
        var args = call.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var cwd = ClaudeHook.Str(args, "Cwd") ?? FirstWorkspace(input) ?? "";
        // ask_question is a question, not a permission: answering it happens in agy itself.
        var question = tool == "ask_question";
        return new Approval(ApprovalStore.NewId(), "Antigravity", ClaudeHook.Str(input, "conversationId") ?? "", ClaudeHook.ProjectName(cwd),
            CursorHook.ShownCwd(cwd), ToolTitle(tool), question ? ClaudeHook.QuestionDetail(args, "Antigravity") : Detail(tool, args),
            ClaudeHook.Str(args, "Description"), now, now + Math.Clamp(timeoutSec, 10, 3600) * 1000L, question ? "question" : "permission");
    }

    private static string? FirstWorkspace(JsonElement input) =>
        input.TryGetProperty("workspacePaths", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).FirstOrDefault(p => !string.IsNullOrEmpty(p))
            : null;

    private static string ToolTitle(string tool) => tool switch
    {
        "run_command" => "Run a command",
        "write_to_file" => "Write a file",
        "replace_file_content" or "multi_replace_file_content" => "Edit a file",
        "read_url_content" => "Fetch a web page",
        "search_web" => "Search the web",
        "ask_question" => "Antigravity has a question",
        _ => "Use " + tool,
    };

    private static string Detail(string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "run_command" => ClaudeHook.Str(args, "CommandLine"),
            "write_to_file" => ClaudeHook.Str(args, "TargetFile") is { } f ? f + "\n\n" + ClaudeHook.Head(ClaudeHook.Str(args, "CodeContent") ?? "", 12) : null,
            "replace_file_content" => ClaudeHook.Str(args, "TargetFile") is { } f ? f + "\n\n" + ClaudeHook.Head(ClaudeHook.Str(args, "ReplacementContent") ?? "", 12) : null,
            "multi_replace_file_content" => ClaudeHook.Str(args, "TargetFile"),
            "read_url_content" => ClaudeHook.Str(args, "Url"),
            "search_web" => ClaudeHook.Str(args, "query"),
            _ => null,
        };
        s ??= JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    // ---------------------------------------------------------------- install / remove the hook in ~/.gemini/config/hooks.json

    /// <summary>
    /// curl prints the agent's decision; when the agent is down or answers with an error, the fallback prints "allow"
    /// so agy asks as usual instead of reading an empty reply as a denial. agy runs hooks with sh -c (cmd /c on Windows).
    /// </summary>
    internal static string HookCommand(int port, int timeoutSec, bool windows) =>
        $"{(windows ? "curl.exe" : "curl")} -sf --max-time {Math.Clamp(timeoutSec, 10, 3600) + 5} -X POST http://127.0.0.1:{port}{Endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\" || "
        + (windows ? "echo {\"decision\":\"allow\"}" : "echo '{\"decision\":\"allow\"}'");

    public static bool Installed(string? path = null) =>
        HookFile.Read(path ?? HooksPath)?[HookName]?["PreToolUse"] is JsonArray groups
        && groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(h => (h?["command"] as JsonValue)?.TryGetValue<string>(out var c) == true && c.Contains(Endpoint)));

    /// <summary>Adds (or, with install = false, removes) the Sidelight hook, keeping every other hook and a backup.</summary>
    public static string? Configure(bool install, int port, int timeoutSec, string? path = null) =>
        HookFile.Update(path ?? HooksPath, install, root =>
        {
            root.Remove(HookName);
            if (root[LegacyHookName]?.ToJsonString().Contains(Endpoint, StringComparison.Ordinal) == true) root.Remove(LegacyHookName);
            if (install)
                root[HookName] = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray(new JsonObject
                    {
                        ["matcher"] = Matcher,
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = HookCommand(port, timeoutSec, OperatingSystem.IsWindows()),
                            ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                        }),
                    }),
                };
        });
}
