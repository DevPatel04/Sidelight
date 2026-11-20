using System.Text.Json;
using System.Text.Json.Nodes;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// The Antigravity CLI hook: agy's tool calls reach the phone as readable requests, the phone's answer becomes an agy
/// decision (and no answer hands the call back to agy), and installing the hook leaves the user's other hooks alone.
/// </summary>
public sealed class AntigravityHookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidelight-agy-" + Guid.NewGuid().ToString("N"));
    private string HooksFile => Path.Combine(_dir, "config", "hooks.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ACommandShowsTheCommandLineAndTheProjectFolder()
    {
        var a = AntigravityHook.Describe(Json("""
            {"toolCall":{"name":"run_command","args":{"CommandLine":"npm test","Cwd":"/work/shop-api","WaitMsBeforeAsync":5000}},
             "stepIdx":3,"conversationId":"c1","workspacePaths":["/work/other"]}
            """), 1000, 30)!;
        Assert.Equal("Antigravity", a.Source);
        Assert.Equal("c1", a.Session);
        Assert.Equal("shop-api", a.Project);
        Assert.Equal("Run a command", a.Tool);
        Assert.Equal("npm test", a.Detail);
        Assert.Equal("permission", a.Kind);
        Assert.Equal(31_000, a.ExpiresAt);
    }

    [Fact]
    public void AFileWriteShowsThePathAndTheStartOfTheContentAndFallsBackToTheWorkspace()
    {
        var a = AntigravityHook.Describe(Json("""
            {"toolCall":{"name":"write_to_file","args":{"TargetFile":"/work/site/a.txt","CodeContent":"hello","Description":"Add a note"}},
             "workspacePaths":["/work/site"]}
            """), 0, 30)!;
        Assert.Equal("Write a file", a.Tool);
        Assert.Equal("/work/site/a.txt\n\nhello", a.Detail);
        Assert.Equal("Add a note", a.Description);
        Assert.Equal("site", a.Project);
    }

    [Fact]
    public void AQuestionIsShownAsAQuestion()
    {
        var a = AntigravityHook.Describe(Json("""{"toolCall":{"name":"ask_question","args":{}}}"""), 0, 30)!;
        Assert.Equal("question", a.Kind);
        Assert.Contains("Antigravity is asking", a.Detail);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"toolCall":"run_command"}""")]
    [InlineData("""{"toolCall":{"args":{}}}""")]
    [InlineData("[]")]
    public void InputThatIsNotAToolCallIsNotShown(string input) =>
        Assert.Null(AntigravityHook.Describe(Json(input), 0, 30));

    [Theory]
    [InlineData("allow", "auto_approve")]
    [InlineData("deny", "deny")]
    [InlineData("skip", "allow")]
    [InlineData(null, "allow")]
    public void ThePhonesAnswerBecomesAnAgyDecision(string? answer, string decision) =>
        Assert.Equal(decision, JsonSerializer.SerializeToElement(AntigravityHook.Output(answer)).GetProperty("decision").GetString());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHookCommandStillPrintsADecisionWhenTheAgentIsDown(bool windows)
    {
        // agy reads an empty reply as a denial, so curl's failure must fall back to "allow" (agy then asks itself).
        var cmd = AntigravityHook.HookCommand(8765, 30, windows);
        Assert.Contains("http://127.0.0.1:8765" + AntigravityHook.Endpoint, cmd);
        Assert.Contains(" -sf ", cmd);
        Assert.Contains("--max-time 35", cmd);
        Assert.EndsWith(windows ? "|| echo {\"decision\":\"allow\"}" : "|| echo '{\"decision\":\"allow\"}'", cmd);
    }

    [Fact]
    public void InstallingKeepsOtherHooksAndRemovingTakesOnlyOurs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        File.WriteAllText(HooksFile, """
            {
              // the user's own linter
              "my-linter-hook": { "PostToolUse": [ { "matcher": "run_command", "hooks": [ { "command": "./lint.sh" } ] } ] },
            }
            """);
        Assert.False(AntigravityHook.Installed(HooksFile));

        Assert.Null(AntigravityHook.Configure(true, 8765, 30, HooksFile));
        Assert.True(AntigravityHook.Installed(HooksFile));
        Assert.True(File.Exists(HooksFile + ".sidelight.bak"));
        var root = JsonNode.Parse(File.ReadAllText(HooksFile))!;
        Assert.NotNull(root["my-linter-hook"]);
        var group = root["sidelight"]!["PreToolUse"]![0]!;
        Assert.Equal(AntigravityHook.Matcher, group["matcher"]!.GetValue<string>());
        Assert.Equal(45, group["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Matches(group["matcher"]!.GetValue<string>(), "run_command");
        Assert.DoesNotMatch(group["matcher"]!.GetValue<string>(), "view_file");

        // installing twice leaves one entry
        Assert.Null(AntigravityHook.Configure(true, 8765, 60, HooksFile));
        Assert.Single(JsonNode.Parse(File.ReadAllText(HooksFile))!["sidelight"]!["PreToolUse"]!.AsArray());

        Assert.Null(AntigravityHook.Configure(false, 8765, 30, HooksFile));
        Assert.False(AntigravityHook.Installed(HooksFile));
        root = JsonNode.Parse(File.ReadAllText(HooksFile))!;
        Assert.Null(root["sidelight"]);
        Assert.NotNull(root["my-linter-hook"]);
    }

    [Fact]
    public void TheHookFromBeforeTheRenameIsReplacedButAnotherPhoneboxEntryIsKept()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        var ours = AntigravityHook.HookCommand(8765, 30, false).Replace("\"", "\\\"");
        File.WriteAllText(HooksFile, $$"""{ "phonebox": { "PreToolUse": [ { "hooks": [ { "command": "{{ours}}" } ] } ] } }""");
        Assert.Null(AntigravityHook.Configure(true, 8765, 30, HooksFile));
        var root = JsonNode.Parse(File.ReadAllText(HooksFile))!;
        Assert.Null(root["phonebox"]);
        Assert.NotNull(root["sidelight"]);

        File.WriteAllText(HooksFile, """{ "phonebox": { "PreToolUse": [ { "hooks": [ { "command": "./other.sh" } ] } ] } }""");
        Assert.Null(AntigravityHook.Configure(true, 8765, 30, HooksFile));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(HooksFile))!["phonebox"]);
    }

    [Fact]
    public void InstallingCreatesTheFileAndRemovingWithoutOneDoesNothing()
    {
        Assert.Null(AntigravityHook.Configure(false, 8765, 30, HooksFile));
        Assert.False(File.Exists(HooksFile));
        Assert.Null(AntigravityHook.Configure(true, 8765, 30, HooksFile));
        Assert.True(AntigravityHook.Installed(HooksFile));
    }

    [Fact]
    public void AFileThatIsNotAnObjectIsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HooksFile)!);
        File.WriteAllText(HooksFile, "[1,2]");
        Assert.NotNull(AntigravityHook.Configure(true, 8765, 30, HooksFile));
        Assert.Equal("[1,2]", File.ReadAllText(HooksFile));
    }
}
