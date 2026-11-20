using System.Text.Json;
using System.Text.Json.Nodes;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// The Cursor hooks: commands and MCP calls reach the phone as readable requests, every answer only ever makes Cursor
/// stricter than without the hook, and installing the hooks leaves the user's other hooks alone.
/// </summary>
public sealed class CursorHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidelight-cursor-" + Guid.NewGuid().ToString("N"));
    private string HooksFile => Path.Combine(_dir, ".cursor", "hooks.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ACommandShowsTheCommandAndTheWorkspaceWhenCwdIsEmpty()
    {
        // Cursor 2.6–3.21 send an empty cwd; the project only shows in workspace_roots.
        var a = CursorHook.Describe(Json("""
            {"hook_event_name":"beforeShellExecution","command":"git push","cwd":"","sandbox":true,
             "conversation_id":"c1","workspace_roots":["/work/shop-api"]}
            """), 1000, 60)!;
        Assert.Equal("Cursor", a.Source);
        Assert.Equal("c1", a.Session);
        Assert.Equal("shop-api", a.Project);
        Assert.Equal("Run a command", a.Tool);
        Assert.Equal("git push", a.Detail);
        Assert.Equal("permission", a.Kind);
        Assert.Equal(61_000, a.ExpiresAt);
    }

    [Fact]
    public void AnMcpCallShowsTheServerToolAndItsArguments()
    {
        var a = CursorHook.Describe(Json("""
            {"hook_event_name":"beforeMCPExecution","tool_name":"create_issue","mcp_server_name":"github",
             "tool_input":"{\"title\":\"Bug\"}","cwd":"/work/site"}
            """), 0, 60)!;
        Assert.Equal("Use github › create_issue", a.Tool);
        Assert.Contains("\"title\": \"Bug\"", a.Detail);
        Assert.Equal("site", a.Project);
    }

    [Fact]
    public void ArgumentsThatAreNotJsonAreShownAsTheyCame()
    {
        var a = CursorHook.Describe(Json("""{"hook_event_name":"beforeMCPExecution","tool_name":"t","tool_input":"not json"}"""), 0, 60)!;
        Assert.Equal("Use t", a.Tool);
        Assert.Equal("not json", a.Detail);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"hook_event_name":"beforeShellExecution","command":""}""")]
    [InlineData("""{"hook_event_name":"beforeMCPExecution"}""")]
    [InlineData("""{"hook_event_name":"afterFileEdit","command":"x"}""")]
    [InlineData("[]")]
    public void InputThatIsNeitherACommandNorAnMcpCallIsNotShown(string input) =>
        Assert.Null(CursorHook.Describe(Json(input), 0, 60));

    [Theory]
    [InlineData("allow", "allow")]
    [InlineData("deny", "deny")]
    [InlineData("skip", "ask")]
    [InlineData(null, "ask")] // shown and nobody answered: Cursor asks in the editor instead of running it unattended
    public void ThePhonesAnswerBecomesACursorPermission(string? answer, string permission)
    {
        var e = JsonSerializer.SerializeToElement(CursorHook.Output(answer));
        Assert.Equal(permission, e.GetProperty("permission").GetString());
        Assert.True(e.GetProperty("continue").GetBoolean());
    }

    [Fact]
    public void WithoutThePhoneNothingChanges() =>
        Assert.Equal("allow", JsonSerializer.SerializeToElement(CursorHook.NoChange).GetProperty("permission").GetString());

    [Fact]
    public void TheHookCommandPostsTheInputToTheLocalAgent()
    {
        var unix = CursorHook.HookCommand(8765, 60, windows: false);
        Assert.StartsWith("curl -sf --max-time 65 -X POST http://127.0.0.1:8765" + CursorHook.Endpoint, unix);
        Assert.EndsWith("--data-binary @-", unix);
        // PowerShell hands the hook's input on only through $input, and @ needs quoting there.
        var win = CursorHook.HookCommand(8765, 60, windows: true);
        Assert.StartsWith("$input | curl.exe ", win);
        Assert.EndsWith("--data-binary '@-'", win);
    }

    [Fact]
    public void InstallingKeepsOtherHooksAndRemovingTakesOnlyOurs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        File.WriteAllText(HooksFile, """
            {
              "version": 1,
              "hooks": {
                // the user's own guard
                "beforeShellExecution": [ { "command": "./block-git.sh", "matcher": "git" } ],
                "afterFileEdit": [ { "command": "./format.sh" } ],
              }
            }
            """);
        Assert.False(CursorHook.Installed(HooksFile));

        Assert.Null(CursorHook.Configure(true, 8765, 60, HooksFile));
        Assert.True(CursorHook.Installed(HooksFile));
        Assert.True(File.Exists(HooksFile + ".sidelight.bak"));
        var hooks = JsonNode.Parse(File.ReadAllText(HooksFile))!["hooks"]!;
        Assert.Equal(2, hooks["beforeShellExecution"]!.AsArray().Count);
        Assert.Single(hooks["beforeMCPExecution"]!.AsArray());
        Assert.Equal(75, hooks["beforeMCPExecution"]![0]!["timeout"]!.GetValue<int>());
        Assert.NotNull(hooks["afterFileEdit"]);

        // installing twice leaves one entry per event
        Assert.Null(CursorHook.Configure(true, 8765, 30, HooksFile));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(HooksFile))!["hooks"]!["beforeShellExecution"]!.AsArray().Count);

        Assert.Null(CursorHook.Configure(false, 8765, 60, HooksFile));
        Assert.False(CursorHook.Installed(HooksFile));
        var root = JsonNode.Parse(File.ReadAllText(HooksFile))!;
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.Equal("./block-git.sh", root["hooks"]!["beforeShellExecution"]![0]!["command"]!.GetValue<string>());
        Assert.Null(root["hooks"]!["beforeMCPExecution"]);
        Assert.NotNull(root["hooks"]!["afterFileEdit"]);
    }

    [Fact]
    public void InstallingCreatesAVersionedFileAndRemovingWithoutOneDoesNothing()
    {
        Assert.Null(CursorHook.Configure(false, 8765, 60, HooksFile));
        Assert.False(File.Exists(HooksFile));
        Assert.Null(CursorHook.Configure(true, 8765, 60, HooksFile));
        Assert.True(CursorHook.Installed(HooksFile));
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(HooksFile))!["version"]!.GetValue<int>());
    }

    [Fact]
    public void AFileThatIsNotAnObjectIsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        File.WriteAllText(HooksFile, "[1,2]");
        Assert.NotNull(CursorHook.Configure(true, 8765, 60, HooksFile));
        Assert.Equal("[1,2]", File.ReadAllText(HooksFile));
    }
}
