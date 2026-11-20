using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sidelight.Agent;

/// <summary>
/// Cursor integration: beforeShellExecution and beforeMCPExecution hooks in ~/.cursor/hooks.json post the agent's
/// commands and MCP calls to the agent, which shows them on the phone.
/// </summary>
/// <remarks>
/// Cursor runs these hooks before its own approval and, as of 3.21, enforces "deny" in every mode but honours "allow"
/// and "ask" only in some (Allowlist and Run Everything; Auto-review runs what it thinks is safe). So the mapping only
/// ever makes Cursor stricter than without the hook: Deny blocks, Allow lets Cursor carry on as it would, a request
/// nobody answered asks in the editor, and with no phone around (or the hook switched off) nothing changes.
/// A failing hook (agent down, timeout) fails open in Cursor, which is the same "nothing changes".
/// </remarks>
public static class CursorHook
{
    public const string Endpoint = "/api/hooks/cursor/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string HooksPath = Path.Combine(Home, ".cursor", "hooks.json");
    private static readonly string[] Events = ["beforeShellExecution", "beforeMCPExecution"];

    internal static readonly object NoChange = new { @continue = true, permission = "allow" };

    /// <summary>Handles one hook call; always returns a permission for the hook to print.</summary>
    public static async Task<object> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals, CancellationToken aborted)
    {
        var c = cfg.Get().Cursor;
        if (!c.PhoneApprovals || !ApprovalStore.PhoneConnected) return NoChange;
        if (c.OnlyWhenAway && Idle.Seconds() < 60) return NoChange;
        if (Describe(input, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), c.TimeoutSec) is not { } a) return NoChange;
        return Output(await approvals.AskAsync(a, aborted));
    }

    /// <summary>The phone's answer as Cursor hook output; null means it was shown and nobody answered.</summary>
    internal static object Output(string? decision) => decision switch
    {
        "allow" => new { @continue = true, permission = "allow" },
        "deny" => new { @continue = true, permission = "deny", user_message = "Denied from the Sidelight panel.", agent_message = "The user denied this from their phone. Do not retry it; ask them what to do instead." },
        _ => new { @continue = true, permission = "ask", user_message = "Nobody answered on the phone." },
    };

    /// <summary>The request as the phone shows it, or null when the input is neither a command nor an MCP call.</summary>
    internal static Approval? Describe(JsonElement input, long now, int timeoutSec)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var ev = ClaudeHook.Str(input, "hook_event_name");
        var command = ClaudeHook.Str(input, "command");
        var mcpTool = ClaudeHook.Str(input, "tool_name");
        string tool, detail;
        if (ev == "beforeMCPExecution" || (ev is null && mcpTool is { Length: > 0 }))
        {
            if (string.IsNullOrEmpty(mcpTool)) return null;
            var server = ClaudeHook.Str(input, "mcp_server_name") ?? ClaudeHook.Str(input, "server");
            tool = "Use " + (server is { Length: > 0 } ? server + " › " : "") + mcpTool;
            detail = McpInput(input);
        }
        else if (ev is null or "beforeShellExecution" && !string.IsNullOrEmpty(command))
        {
            tool = "Run a command";
            detail = command!;
        }
        else return null;
        if (detail.Length > 3000) detail = detail[..3000] + "…";

        var cwd = ClaudeHook.Str(input, "cwd") is { Length: > 0 } d ? d : FirstWorkspace(input) ?? "";
        return new Approval(ApprovalStore.NewId(), "Cursor", ClaudeHook.Str(input, "conversation_id") ?? "", ClaudeHook.ProjectName(cwd),
            tool, detail, null, now, now + Math.Clamp(timeoutSec, 10, 3600) * 1000L);
    }

    /// <summary>Cursor sends the MCP arguments as a JSON string; shown indented when it parses.</summary>
    private static string McpInput(JsonElement input)
    {
        if (!input.TryGetProperty("tool_input", out var ti)) return "";
        try
        {
            var e = ti.ValueKind == JsonValueKind.String ? JsonDocument.Parse(ti.GetString()!).RootElement : ti;
            return JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return ti.GetString() ?? ""; }
    }

    private static string? FirstWorkspace(JsonElement input) =>
        input.TryGetProperty("workspace_roots", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).FirstOrDefault(p => !string.IsNullOrEmpty(p))
            : null;

    // ---------------------------------------------------------------- install / remove the hooks in ~/.cursor/hooks.json

    /// <summary>
    /// curl prints the agent's answer; if the agent is down curl fails, and Cursor treats a failed hook as "carry on".
    /// Cursor on Windows runs hook commands through PowerShell, which passes the hook's input on only through $input.
    /// </summary>
    internal static string HookCommand(int port, int timeoutSec, bool windows)
    {
        var url = $"http://127.0.0.1:{port}{Endpoint}";
        var maxTime = Math.Clamp(timeoutSec, 10, 3600) + 5;
        return windows
            ? $"$input | curl.exe -sf --max-time {maxTime} -X POST {url} -H 'Content-Type: application/json' --data-binary '@-'"
            : $"curl -sf --max-time {maxTime} -X POST {url} -H \"Content-Type: application/json\" --data-binary @-";
    }

    private static bool IsOurs(JsonNode? hook) =>
        (hook?["command"] as JsonValue)?.TryGetValue<string>(out var c) == true && c.Contains(Endpoint);

    public static bool Installed(string? path = null) =>
        HookFile.Read(path ?? HooksPath)?["hooks"] is JsonObject hooks
        && Events.All(e => hooks[e] is JsonArray list && list.Any(IsOurs));

    /// <summary>Adds (or, with install = false, removes) the Sidelight hooks, keeping every other hook and a backup.</summary>
    public static string? Configure(bool install, int port, int timeoutSec, string? path = null) =>
        HookFile.Update(path ?? HooksPath, install, root =>
        {
            root["version"] ??= 1;
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            foreach (var e in Events)
            {
                var list = hooks[e] as JsonArray ?? new JsonArray();
                foreach (var h in list.Where(IsOurs).ToList()) list.Remove(h);
                if (install)
                    list.Add(new JsonObject
                    {
                        ["command"] = HookCommand(port, timeoutSec, OperatingSystem.IsWindows()),
                        // longer than the agent waits, so the agent's "ask" arrives before Cursor gives up on the hook
                        ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                    });
                if (list.Count > 0) hooks[e] = list; else hooks.Remove(e);
            }
            root["hooks"] = hooks;
        });
}
