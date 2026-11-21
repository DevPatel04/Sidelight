using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sidelight.Agent;
using Xunit;

/// <summary>
/// "Not while I am looking at it": a permission prompt stays on the PC while the app that asks owns the focused
/// window, and reaches the phone as before when focus is elsewhere or can't be told.
/// </summary>
public sealed class WindowFocusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidelight-focus-" + Guid.NewGuid().ToString("N"));

    public WindowFocusTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // curl (50) ← sh (40) ← claude (30) ← the terminal (20) ← init (1)
    private static readonly Dictionary<int, int> Tree = new() { [50] = 40, [40] = 30, [30] = 20, [20] = 1, [1] = 0, [77] = 1 };
    private static int? Parent(int pid) => Tree.TryGetValue(pid, out var p) ? p : null;

    [Theory]
    [InlineData(20, true)]  // the terminal that runs the agent is focused
    [InlineData(30, true)]  // the agent's own window (an editor)
    [InlineData(50, true)]  // the caller itself
    [InlineData(77, false)] // another app is focused
    public void TheCallerIsInFrontWhenTheFocusedProcessIsOneOfItsAncestors(int focused, bool expected) =>
        Assert.Equal(expected, WindowFocus.DescendsFrom(50, focused, Parent));

    [Fact]
    public void ABrokenChainOrAnUnknownCallerCannotBeTold()
    {
        Assert.Null(WindowFocus.DescendsFrom(60, 20, Parent));  // the caller's parent can't be read
        Assert.Null(WindowFocus.DescendsFrom(0, 20, Parent));
        Assert.Null(WindowFocus.DescendsFrom(50, 0, Parent));
    }

    [Fact]
    public void AParentLoopEndsTheWalk()
    {
        var loop = new Dictionary<int, int> { [5] = 6, [6] = 5 };
        Assert.False(WindowFocus.DescendsFrom(5, 9, p => loop.TryGetValue(p, out var x) ? x : null));
    }

    [Fact]
    public void TheClientSocketIsFoundInProcNetTcp()
    {
        // client 127.0.0.1:51234 (0xC822) → agent 127.0.0.1:8765 (0x223D); the listening and server-side rows don't match
        const string table = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:223D 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 1111 1 0000000000000000 100 0 0 10 0
               1: 0100007F:223D 0100007F:C822 01 00000000:00000000 00:00000000 00000000  1000        0 2222 1 0000000000000000 20 4 30 10 -1
               2: 0100007F:C822 0100007F:223D 01 00000000:00000000 00:00000000 00000000  1000        0 3333 1 0000000000000000 20 4 30 10 -1
            """;
        Assert.Equal("3333", WindowFocus.SocketInode(table, 51234, 8765));
        Assert.Null(WindowFocus.SocketInode(table, 51235, 8765));
        Assert.Null(WindowFocus.SocketInode(null, 51234, 8765));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]  // the option is off
    [InlineData(true, false, false)]  // focus is elsewhere
    [InlineData(true, null, false)]   // can't be told: the phone asks as before
    public void ThePcKeepsThePromptOnlyWhenTheAskerIsKnownToBeInFront(bool option, bool? inFront, bool expected) =>
        Assert.Equal(expected, ClaudeHook.LeaveToPc(option, () => inFront));

    [Fact]
    public void TheOptionIsOnUnlessTheSettingsTurnItOff()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"claude":{"phoneApprovals":true},"cursor":{"skipWhenFocused":false}}""");
        var c = new ConfigStore(path).Get();
        Assert.True(c.Claude.SkipWhenFocused);
        Assert.False(c.Cursor.SkipWhenFocused);
        Assert.True(c.Antigravity.SkipWhenFocused);
        Assert.True(c.Display.AlertSound);
    }

    private (ConfigStore Cfg, ApprovalStore Approvals, SessionMonitor Sessions) Setup(bool skipWhenFocused)
    {
        var path = Path.Combine(_dir, "config.json");
        var on = skipWhenFocused ? "true" : "false";
        File.WriteAllText(path, $$$"""
            {"claude":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30,"skipWhenFocused":{{{on}}}},
             "cursor":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30,"skipWhenFocused":{{{on}}}},
             "antigravity":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30,"skipWhenFocused":{{{on}}}}}
            """);
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push.json"));
        return (new ConfigStore(path), new ApprovalStore(push, NullLogger<ApprovalStore>.Instance), new SessionMonitor(NullLogger<SessionMonitor>.Instance));
    }

    private const string ClaudeInput = """{"tool_name":"Bash","tool_input":{"command":"git push"},"cwd":"/work/shop","session_id":"s1"}""";
    private const string CursorInput = """{"hook_event_name":"beforeShellExecution","command":"git push","workspace_roots":["/work/shop"]}""";
    private const string AgyInput = """{"toolCall":{"name":"run_command","args":{"CommandLine":"git push","Cwd":"/work/shop"}}}""";

    [Fact]
    public async Task EachHookLeavesThePromptToThePcWhileTheAskerIsFocused()
    {
        var (cfg, approvals, sessions) = Setup(skipWhenFocused: true);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var asked = 0;
        bool? InFront() { asked++; return true; }

        Assert.Null(await ClaudeHook.HandleAsync(Json(ClaudeInput), cfg, approvals, sessions, CancellationToken.None, InFront));
        Assert.Null(await CodexHook.HandleAsync(Json(ClaudeInput), cfg, approvals, sessions, CancellationToken.None, InFront));
        Assert.Same(CursorHook.NoChange, await CursorHook.HandleAsync(Json(CursorInput), cfg, approvals, CancellationToken.None, InFront));
        Assert.Same(AntigravityHook.NoDecision, await AntigravityHook.HandleAsync(Json(AgyInput), cfg, approvals, CancellationToken.None, InFront));
        Assert.Equal(4, asked);
        Assert.Empty(approvals.Snapshot()); // nothing ever reached the phone
    }

    [Theory]
    [InlineData(true, false)] // focus is elsewhere
    [InlineData(true, null)]  // focus can't be told (Wayland, macOS)
    [InlineData(false, true)] // the option is off: the phone asks even while you look at the terminal
    public async Task OtherwiseThePhoneAsksAsBefore(bool option, bool? inFront)
    {
        var (cfg, approvals, sessions) = Setup(option);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var cts = new CancellationTokenSource();
        var asking = ClaudeHook.HandleAsync(Json(ClaudeInput), cfg, approvals, sessions, cts.Token, () => inFront);
        var a = Assert.Single(approvals.Snapshot());
        Assert.Equal("git push", a.Detail);
        Assert.True(approvals.Decide(a.Id, "allow"));
        Assert.NotNull(await asking);
    }
}
