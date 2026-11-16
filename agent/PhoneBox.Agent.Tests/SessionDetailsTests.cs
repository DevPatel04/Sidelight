using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class SessionDetailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-sessions-" + Guid.NewGuid());
    private readonly SessionMonitor _monitor = new(NullLogger<SessionMonitor>.Instance);
    private string Rollout(int daysAgo = 0)
    {
        var day = DateTime.Now.AddDays(-daysAgo);
        var dir = Path.Combine(_root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rollout-2026-10-01-12345678-abcd.jsonl");
        File.WriteAllText(path, Line("session_meta", new { cwd = "/work/project" }));
        return path;
    }
    private static string Line(string type, object payload) => JsonSerializer.Serialize(new { type, timestamp = DateTimeOffset.UtcNow, payload }) + "\n";

    [Fact]
    public void ResumedSessionIsFoundInAnOldDirectory()
    {
        var file = Rollout(40);
        File.AppendAllText(file, Line("turn_context", new { model = "test-model", effort = "high" }) + Line("event_msg", new { type = "task_started" }));
        var session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal("test-model", session.Model);
        Assert.Equal("project", session.Project);
        Assert.Equal("busy", session.Status);
    }

    [Fact]
    public void FollowsAppendsAndCompletionWithoutSendingPromptsOrToolArguments()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("turn_context", new { model = "test-model", effort = "high" })
            + Line("event_msg", new { type = "task_started" })
            + Line("event_msg", new { type = "user_message", message = "PRIVATE PROMPT" })
            + Line("response_item", new { type = "function_call", name = "read", arguments = "PRIVATE ARGUMENT" })
            + Line("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = 123, input_tokens = 100, output_tokens = 23 }, model_context_window = 1000 } }));
        var session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal(123, session.TotalTokens);
        Assert.Equal("read", session.LastTool);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(session));
        File.AppendAllText(file, Line("event_msg", new { type = "task_complete" }));
        session = Assert.Single(_monitor.ReadCodex(_root));
        Assert.Equal("completed", session.Outcome);
        Assert.Equal("idle", session.Status);
        Assert.Equal(123, session.TotalTokens);
    }

    [Fact]
    public void MalformedTokenCountersDoNotHideTheSession()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("event_msg", new { type = "task_started" })
            + Line("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = "unknown" } } }));
        Assert.Single(_monitor.ReadCodex(_root));
    }

    [Fact]
    public void CompletesLongPartialRecordsOnTheNextRead()
    {
        var file = Rollout();
        File.AppendAllText(file, Line("event_msg", new { type = "task_started" }));
        Assert.Single(_monitor.ReadCodex(_root));
        var line = Line("response_item", new { type = "function_call", name = "read", arguments = new string('x', 90000) });
        File.AppendAllText(file, line[..85000]);
        Assert.Null(Assert.Single(_monitor.ReadCodex(_root)).LastTool);
        File.AppendAllText(file, line[85000..]);
        Assert.Equal("read", Assert.Single(_monitor.ReadCodex(_root)).LastTool);
        File.WriteAllText(file, Line("session_meta", new { cwd = "/work/project" }) + Line("event_msg", new { type = "turn_aborted" }));
        Assert.Equal("aborted", Assert.Single(_monitor.ReadCodex(_root)).Outcome);
    }

    [Fact]
    public void Claude_NormalSession_IsReported()
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        var json = JsonSerializer.Serialize(new
        {
            pid = 1122,
            sessionId = "sess-1",
            cwd = "/Users/test/workspace/my-app",
            status = "waiting",
            waitingFor = "permission prompt"
        });
        File.WriteAllText(Path.Combine(claudeDir, "1122.json"), json);

        var s = Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 1122));
        Assert.Equal("claude", s.Tool);
        Assert.Equal("my-app", s.Project);
        Assert.Equal("waiting", s.Status);
        Assert.Equal("permission prompt", s.WaitingFor);
        Assert.Equal(1122, s.Pid);
    }

    [Fact]
    public void Claude_ClaudeProbeAndCodexBar_AreIgnored()
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        var probeJson = JsonSerializer.Serialize(new
        {
            pid = 2233,
            sessionId = "sess-probe",
            cwd = "/Users/test/Library/Application Support/CodexBar/ClaudeProbe",
            status = "waiting",
            waitingFor = "dialog open"
        });
        File.WriteAllText(Path.Combine(claudeDir, "2233.json"), probeJson);

        Assert.Empty(SessionMonitor.ReadClaude(_root, pid => pid == 2233));
    }

    [Theory]
    [InlineData("/Users/test/Library/Application Support/CodexBar/ClaudeProbe", true)]
    [InlineData("/var/folders/xy/T/CodexBar/ClaudeProbe/", true)]
    [InlineData(@"C:\Users\test\AppData\Local\Temp\CodexBar\ClaudeProbe", true)]
    [InlineData("/Users/test/Projects/CodexBar", false)]
    [InlineData("/Users/test/Projects/CodexBar/Sources", false)]
    [InlineData("/Users/test/Projects/ClaudeProbeKit", false)]
    [InlineData("/Users/test/CodexBar/ClaudeProbe/subdir", false)]
    public void Claude_OnlyTheExactCodexBarProbeDirectoryIsIgnored(string cwd, bool ignored)
    {
        var claudeDir = Path.Combine(_root, ".claude", "sessions");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "3344.json"),
            JsonSerializer.Serialize(new { pid = 3344, sessionId = "sess-x", cwd, status = "busy" }));

        var sessions = SessionMonitor.ReadClaude(_root, pid => pid == 3344).ToList();
        Assert.Equal(ignored ? 0 : 1, sessions.Count);
    }

    [Fact]
    public void Antigravity_NeverParsesDbWalAndReadsDocumentedDaemonWorkspaceSafely()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        var convDir = Path.Combine(agyDir, "conversations");
        Directory.CreateDirectory(daemonDir);
        Directory.CreateDirectory(convDir);

        // Even if conversations/conversation_summaries.db-wal was just written and contains file:/// URIs, it must never be parsed or mark the session busy.
        var wal = Path.Combine(convDir, "conversation_summaries.db-wal");
        File.WriteAllText(wal, "file:///Users/test/Projects/wal-secret\u0001\u0002 ");
        File.SetLastWriteTimeUtc(wal, DateTime.UtcNow);

        var daemonPath = Path.Combine(daemonDir, "daemon.json");
        var daemonTime = DateTime.UtcNow.AddMinutes(-15);
        File.WriteAllText(daemonPath, """{"pid":4455}""");
        File.SetLastWriteTimeUtc(daemonPath, daemonTime);
        bool Match4455(int pid, long _) => pid == 4455;

        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 9999, sameStart: Match4455));

        var session = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 4455, sameStart: Match4455, getProcessStartMs: _ => null));
        Assert.Equal("antigravity", session.Tool);
        Assert.Equal("Antigravity", session.Project);
        Assert.Equal("idle", session.Status);
        Assert.Equal(4455, session.Pid);
        Assert.Equal(session.StartedAt, session.Since);

        // Percent-encoded documented workspace field is decoded cleanly.
        File.WriteAllText(daemonPath, """{"pid":4455,"workspace":"file:///Users/test/Projects/my%20phonebox"}""");
        File.SetLastWriteTimeUtc(daemonPath, daemonTime.AddSeconds(1));
        var withWorkspace = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 4455, sameStart: Match4455, getProcessStartMs: _ => null));
        Assert.Equal("my phonebox", withWorkspace.Project);

        // Control characters (raw or percent-encoded) are rejected back to neutral fallback.
        File.WriteAllText(daemonPath, """{"pid":4455,"cwd":"/Users/test/bad%01proj"}""");
        File.SetLastWriteTimeUtc(daemonPath, daemonTime.AddSeconds(2));
        var withBadWorkspace = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 4455, sameStart: Match4455, getProcessStartMs: _ => null));
        Assert.Equal("Antigravity", withBadWorkspace.Project);
    }

    [Fact]
    public void Antigravity_DaemonFallbackRejectsStaleRecycledPidViaSameStart()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var pid = proc.Id;
        var daemonPath = Path.Combine(daemonDir, "hub_stale.json");
        File.WriteAllText(daemonPath, JsonSerializer.Serialize(new { pid }));
        // Stale daemon file from 10 minutes before the current process started -> PID reuse rejected.
        File.SetLastWriteTimeUtc(daemonPath, proc.StartTime.ToUniversalTime().AddMinutes(-10));
        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => []));

        // When the file timestamp matches the live process's start time, it is accepted.
        File.SetLastWriteTimeUtc(daemonPath, proc.StartTime.ToUniversalTime());
        var session = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => []));
        Assert.Equal(pid, session.Pid);
    }

    [Fact]
    public void Antigravity_DaemonRewrittenLaterWithMatchingProcStartKeepsProject()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        var startedUtc = DateTime.UtcNow.AddHours(-3);
        var startedFt = startedUtc.ToFileTimeUtc();
        var startedUnixSec = new DateTimeOffset(startedUtc).ToUnixTimeSeconds();
        var expectedSecFt = DateTimeOffset.FromUnixTimeSeconds(startedUnixSec).UtcDateTime.ToFileTimeUtc();
        var startedUnixMs = new DateTimeOffset(startedUtc).ToUnixTimeMilliseconds();
        var expectedMsFt = DateTimeOffset.FromUnixTimeMilliseconds(startedUnixMs).UtcDateTime.ToFileTimeUtc();

        var daemonPath = Path.Combine(daemonDir, "hub_rewrite.json");

        // 1) procStart as Unix seconds number, file rewritten 3 hours after process start -> still accepted!
        File.WriteAllText(daemonPath, JsonSerializer.Serialize(new { pid = 4455, procStart = startedUnixSec, workspace = "file:///Users/test/phonebox" }));
        File.SetLastWriteTimeUtc(daemonPath, DateTime.UtcNow);
        var sNum = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [],
            isAlive: pid => pid == 4455,
            sameStart: (pid, ft) => pid == 4455 && ft == expectedSecFt,
            getProcessStartMs: _ => startedUnixMs));
        Assert.Equal("phonebox", sNum.Project);

        // 2) procStart as string (Unix milliseconds), file rewritten later -> accepted!
        File.WriteAllText(daemonPath, JsonSerializer.Serialize(new { pid = 4455, procStart = startedUnixMs.ToString(), folder = "/Users/test/phonebox-str" }));
        File.SetLastWriteTimeUtc(daemonPath, DateTime.UtcNow.AddSeconds(1));
        var sStr = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [],
            isAlive: pid => pid == 4455,
            sameStart: (pid, ft) => pid == 4455 && ft == expectedMsFt,
            getProcessStartMs: _ => startedUnixMs));
        Assert.Equal("phonebox-str", sStr.Project);

        // 3) procStart as explicit FILETIME matching startedFt -> accepted; mismatched procStart -> rejected.
        File.WriteAllText(daemonPath, JsonSerializer.Serialize(new { pid = 4455, procStart = startedFt, cwd = "/Users/test/phonebox-ft" }));
        File.SetLastWriteTimeUtc(daemonPath, DateTime.UtcNow.AddSeconds(2));
        var sFt = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [],
            isAlive: pid => pid == 4455,
            sameStart: (pid, ft) => pid == 4455 && ft == startedFt,
            getProcessStartMs: _ => startedUnixMs));
        Assert.Equal("phonebox-ft", sFt.Project);

        Assert.Empty(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [],
            isAlive: pid => pid == 4455,
            sameStart: (_, _) => false));
    }

    [Fact]
    public void Antigravity_DaemonFailuresAndOversizedJsonAreCachedAndSkipped()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        var badPath = Path.Combine(daemonDir, "bad.json");
        var bigPath = Path.Combine(daemonDir, "big.json");

        File.WriteAllText(badPath, """{"pid":"123"}""");
        File.WriteAllText(bigPath, new string(' ', 70 * 1024));
        var badTime = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(badPath, badTime);
        File.SetLastWriteTimeUtc(bigPath, badTime);

        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: _ => true, sameStart: (_, _) => true));
        Assert.Equal(2, _monitor.AgyDaemonParseCount);

        // Second poll with unchanged mtime and length uses cached failure/skip entries without re-parsing.
        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: _ => true, sameStart: (_, _) => true));
        Assert.Equal(2, _monitor.AgyDaemonParseCount);

        // Invalid JSON syntax is also cached by (mtime, length).
        File.WriteAllText(badPath, "{not-valid-json");
        File.SetLastWriteTimeUtc(badPath, badTime.AddSeconds(1));
        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: _ => true, sameStart: (_, _) => true));
        Assert.Equal(3, _monitor.AgyDaemonParseCount);
        Assert.Empty(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: _ => true, sameStart: (_, _) => true));
        Assert.Equal(3, _monitor.AgyDaemonParseCount);
    }

    [Fact]
    public void Antigravity_DoesNotCopyProjectFromNonSelectedDaemon()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        var ft = DateTime.UtcNow.AddMinutes(-5).ToFileTimeUtc();
        File.WriteAllText(Path.Combine(daemonDir, "a_other.json"), """{"pid":100,"workspace":"/Users/test/other-proj"}""");
        File.WriteAllText(Path.Combine(daemonDir, "b_selected.json"), """{"pid":200}""");

        var session = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(200, "Antigravity", ft, 1_700_000_000_000, null)],
            isAlive: pid => pid is 100 or 200,
            sameStart: (_, _) => true));
        Assert.Equal(200, session.Pid);
        Assert.Equal("Antigravity", session.Project);
    }

    [Theory]
    [InlineData("file:///Users/test/my%2520proj", "my%20proj")]
    [InlineData("file:///Users/test/my%20phonebox", "my phonebox")]
    [InlineData("/Users/test/bidi\u202Eproj", null)]
    [InlineData("file:///Users/test/bidi%E2%80%AEproj", null)]
    [InlineData("/Users/test/bad%01proj", null)]
    public void ProjectNameFromPathOrUri_HandlesSingleDecodeBidiAndLengthCap(string input, string? expected)
    {
        Assert.Equal(expected, SessionMonitor.ProjectNameFromPathOrUri(input));
        Assert.Null(SessionMonitor.ProjectNameFromPathOrUri("/Users/test/" + new string('a', 101)));
        Assert.Equal(new string('a', 100), SessionMonitor.ProjectNameFromPathOrUri("/Users/test/" + new string('a', 100)));
    }

    [Fact]
    public void Antigravity_CachesBrainAndConversationsScanFor30Seconds()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var brain1 = Path.Combine(agyDir, "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(brain1);
        var t1 = Path.Combine(brain1, "transcript.jsonl");
        File.WriteAllText(t1, "{}\n");
        File.SetLastWriteTimeUtc(t1, DateTime.UtcNow.AddMinutes(-5));

        var startUtc = DateTime.UtcNow.AddHours(-1);
        var startFt = startUtc.ToFileTimeUtc();
        var startMs = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds();
        long ticks = 100_000;
        var s1 = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(4455, "agy", startFt, startMs, null)],
            isAlive: pid => pid == 4455,
            sameStart: (pid, _) => pid == 4455,
            getTicks: () => ticks));
        Assert.Equal("idle", s1.Status);

        // New conversation created within 30s does not trigger full re-enumeration yet.
        var brain2 = Path.Combine(agyDir, "brain", "conv-2", ".system_generated", "logs");
        Directory.CreateDirectory(brain2);
        var t2 = Path.Combine(brain2, "transcript.jsonl");
        File.WriteAllText(t2, "{}\n");
        File.SetLastWriteTimeUtc(t2, DateTime.UtcNow);

        ticks += 5_000;
        var s2 = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(4455, "agy", startFt, startMs, null)],
            isAlive: pid => pid == 4455,
            sameStart: (pid, _) => pid == 4455,
            getTicks: () => ticks));
        Assert.Equal("idle", s2.Status);

        // Updating an already-discovered candidate file is reflected immediately without rescanning.
        File.SetLastWriteTimeUtc(t1, DateTime.UtcNow);
        ticks += 1_000;
        var s2b = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(4455, "agy", startFt, startMs, null)],
            isAlive: pid => pid == 4455,
            sameStart: (pid, _) => pid == 4455,
            getTicks: () => ticks));
        Assert.Equal("busy", s2b.Status);

        // After 30s, rescan discovers the newer conv-2 file.
        File.SetLastWriteTimeUtc(t1, DateTime.UtcNow.AddMinutes(-5));
        ticks += 30_000;
        var s3 = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(4455, "agy", startFt, startMs, null)],
            isAlive: pid => pid == 4455,
            sameStart: (pid, _) => pid == 4455,
            getTicks: () => ticks));
        Assert.Equal("busy", s3.Status);
    }

    [Fact]
    public void Antigravity_StartedAtIsStableWithoutConversationFilesAndClampsOldSince()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var daemonDir = Path.Combine(agyDir, "daemon");
        var convDir = Path.Combine(agyDir, "conversations");
        Directory.CreateDirectory(daemonDir);
        Directory.CreateDirectory(convDir);
        var daemonPath = Path.Combine(daemonDir, "hub_1.json");
        File.WriteAllText(daemonPath, """{"pid":4455}""");
        var daemonTime = DateTime.UtcNow.AddMinutes(-15);
        File.SetLastWriteTimeUtc(daemonPath, daemonTime);

        // Old .pb file from before daemon started -> Since is clamped to StartedAt.
        var oldPb = Path.Combine(convDir, "old.pb");
        File.WriteAllText(oldPb, "pb");
        File.SetLastWriteTimeUtc(oldPb, daemonTime.AddHours(-4));

        // Without process StartTime, falls back to daemon file timestamp — never drifts to UtcNow.
        var s1 = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 4455, sameStart: (_, _) => true, getProcessStartMs: _ => null));
        var s2 = Assert.Single(_monitor.ReadAntigravity(_root, scanProcesses: () => [], isAlive: pid => pid == 4455, sameStart: (_, _) => true, getProcessStartMs: _ => null));
        Assert.Equal(new DateTimeOffset(daemonTime).ToUnixTimeMilliseconds(), s1.StartedAt);
        Assert.Equal(s1.StartedAt, s2.StartedAt);
        Assert.Equal(s1.StartedAt, s1.Since);

        // Process StartTime is cached per (pid, startKey) and never drifts to UtcNow.
        var procMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var fixedStart = DateTimeOffset.UtcNow.AddHours(-2);
        var fixedStartMs = fixedStart.ToUnixTimeMilliseconds();
        var fixedStartFt = fixedStart.UtcDateTime.ToFileTimeUtc();
        var calls = 0;
        var s3 = Assert.Single(procMonitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(7788, "agy", fixedStartFt, fixedStartMs, null)],
            isAlive: pid => pid == 7788,
            sameStart: (_, _) => true,
            getProcessStartMs: _ => { calls++; return fixedStartMs; }));
        var s4 = Assert.Single(procMonitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(7788, "agy", fixedStartFt, fixedStartMs, null)],
            isAlive: pid => pid == 7788,
            sameStart: (_, _) => true,
            getProcessStartMs: _ => { calls++; return fixedStartMs; }));
        Assert.Equal(1, calls);
        Assert.Equal(fixedStartMs, s3.StartedAt);
        Assert.Equal(fixedStartMs, s4.StartedAt);
    }

    [Fact]
    public void Antigravity_RejectsOtherEditorsSelectsEarliestMainProcessAndThrottlesScans()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        Directory.CreateDirectory(agyDir);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var tLater = t0.AddSeconds(10);
        long ticks = 10_000;
        var scanCount = 0;

        // 1) Unrelated language_server_macos (no antigravity cmdline marker and no daemon) must NEVER become a session.
        Assert.Empty(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () =>
            {
                scanCount++;
                return
                [
                    new AntigravityProcessCandidate(9001, "language_server_macos", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), "language_server_macos --app_data_dir=windsurf"),
                    new AntigravityProcessCandidate(9002, "language_server", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), null),
                ];
            },
            isAlive: _ => true,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal(1, scanCount);

        // 2) Throttles closed-process scan to once per 30 seconds.
        ticks += 29_999;
        Assert.Empty(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => { scanCount++; return []; },
            isAlive: _ => false,
            getTicks: () => ticks));
        Assert.Equal(1, scanCount);

        // 3) After 30 seconds, scans again and accepts Antigravity-marked language_server_macos.
        ticks += 1;
        var cmdReadCount = 0;
        var langServerSession = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () =>
            {
                scanCount++;
                return [new AntigravityProcessCandidate(9003, "language_server_macos", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), null)];
            },
            getCommandLine: pid => { cmdReadCount++; return pid == 9003 ? "language_server_macos --app_data_dir=antigravity --extension_server_port=18765" : null; },
            isAlive: pid => pid == 9003,
            sameStart: (pid, _) => pid == 9003,
            getTicks: () => ticks));
        Assert.Equal(2, scanCount);
        Assert.Equal(1, cmdReadCount);
        Assert.Equal(9003, langServerSession.Pid);

        // Subsequent poll while alive uses cached process without scanning again.
        ticks += 2_000;
        var cachedSession = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => { scanCount++; return []; },
            isAlive: pid => pid == 9003,
            sameStart: (pid, _) => pid == 9003,
            getTicks: () => ticks));
        Assert.Equal(2, scanCount);
        Assert.Equal(9003, cachedSession.Pid);

        // If cached process exits and same (Pid, FileTimeUtc) is rescanned later, cmdline lookup is served from _agyCmdMatchCache.
        ticks += 30_000;
        var exitedFirst = true;
        Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(9003, "language_server_macos", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), null)],
            getCommandLine: _ => { cmdReadCount++; return "should-not-be-called"; },
            isAlive: pid => { if (exitedFirst) { exitedFirst = false; return false; } return pid == 9003; },
            sameStart: (pid, _) => pid == 9003,
            getTicks: () => ticks));
        Assert.Equal(1, cmdReadCount);

        // 4) Priority and earliest-start selection: Antigravity main process (earliest start) beats later Electron helper, agy, and language_server.
        var multiMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var mainSession = Assert.Single(multiMonitor.ReadAntigravity(
            _root,
            scanProcesses: () =>
            [
                new AntigravityProcessCandidate(300, "language_server_macos", t0.AddMinutes(-5).UtcDateTime.ToFileTimeUtc(), t0.AddMinutes(-5).ToUnixTimeMilliseconds(), "--app_data_dir=antigravity"),
                new AntigravityProcessCandidate(200, "agy", t0.AddMinutes(-2).UtcDateTime.ToFileTimeUtc(), t0.AddMinutes(-2).ToUnixTimeMilliseconds(), null),
                new AntigravityProcessCandidate(101, "Antigravity", tLater.UtcDateTime.ToFileTimeUtc(), tLater.ToUnixTimeMilliseconds(), null),
                new AntigravityProcessCandidate(102, "Antigravity", 0, 0, null),
                new AntigravityProcessCandidate(103, "Antigravity IDE", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), null),
            ],
            isAlive: _ => true,
            sameStart: (_, _) => true));
        Assert.Equal(103, mainSession.Pid);

        // 5) Generic language_server without cmdline is accepted when a validated daemon matches its PID.
        var daemonMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        var daemonDir = Path.Combine(agyDir, "daemon");
        Directory.CreateDirectory(daemonDir);
        File.WriteAllText(Path.Combine(daemonDir, "hub_ls.json"), JsonSerializer.Serialize(new { pid = 9004, procStart = t0.ToUnixTimeMilliseconds(), workspace = "/Users/test/ls-proj" }));
        var daemonSession = Assert.Single(daemonMonitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(9004, "language_server_macos", t0.UtcDateTime.ToFileTimeUtc(), t0.ToUnixTimeMilliseconds(), null)],
            isAlive: pid => pid == 9004,
            sameStart: (pid, _) => pid == 9004));
        Assert.Equal(9004, daemonSession.Pid);
        Assert.Equal("ls-proj", daemonSession.Project);
    }

    [Fact]
    public void Antigravity_TranscriptCompletionTransitionsToIdleWithoutTwoMinuteChurn()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var convDir = Path.Combine(agyDir, "conversations");
        var logDir = Path.Combine(agyDir, "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(convDir);
        Directory.CreateDirectory(logDir);
        var transcript = Path.Combine(logDir, "transcript.jsonl");
        var pbPath = Path.Combine(convDir, "conv-1.pb");
        var startUtc = DateTime.UtcNow.AddMinutes(-10);
        var startFt = startUtc.ToFileTimeUtc();
        var startMs = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds();
        long ticks = 50_000;

        // Recent .pb write without a transcript never marks the session busy.
        File.WriteAllText(pbPath, "pb");
        var pbTime = DateTime.UtcNow.AddSeconds(-15);
        File.SetLastWriteTimeUtc(pbPath, pbTime);
        var pbOnly = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(5511, "Antigravity", startFt, startMs, null)],
            isAlive: pid => pid == 5511,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal("idle", pbOnly.Status);
        Assert.Equal(new DateTimeOffset(pbTime).ToUnixTimeMilliseconds(), pbOnly.Since);

        // Turn in flight (USER_INPUT) -> busy (after discovery interval).
        ticks += 30_000;
        File.WriteAllText(transcript, JsonSerializer.Serialize(new { type = "USER_INPUT", content = "Check build" }) + "\n");
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow);
        var busy = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(5511, "Antigravity", startFt, startMs, null)],
            isAlive: pid => pid == 5511,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal("busy", busy.Status);
        Assert.Null(busy.Outcome);

        // PLANNER_RESPONSE with pending tool_calls -> still busy.
        ticks += 2_000;
        File.AppendAllText(transcript, JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", status = "DONE", tool_calls = new[] { new { name = "read_file" } } }) + "\n");
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddSeconds(1));
        var toolInFlight = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(5511, "Antigravity", startFt, startMs, null)],
            isAlive: pid => pid == 5511,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal("busy", toolInFlight.Status);

        // Final PLANNER_RESPONSE (DONE, empty tool_calls) written right now -> immediately idle with Outcome = completed.
        ticks += 2_000;
        File.AppendAllText(transcript, JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", status = "DONE", tool_calls = Array.Empty<object>() }) + "\n");
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddSeconds(2));
        var done = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(5511, "Antigravity", startFt, startMs, null)],
            isAlive: pid => pid == 5511,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal("idle", done.Status);
        Assert.Equal("completed", done.Outcome);

        // Final PLANNER_RESPONSE (ERROR) -> idle with Outcome = aborted; works even on > 64 KB transcript files.
        ticks += 2_000;
        File.WriteAllText(transcript,
            new string(' ', 70 * 1024) + "\n" +
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", status = "ERROR" }) + "\n");
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddSeconds(3));
        var aborted = Assert.Single(_monitor.ReadAntigravity(
            _root,
            scanProcesses: () => [new AntigravityProcessCandidate(5511, "Antigravity", startFt, startMs, null)],
            isAlive: pid => pid == 5511,
            sameStart: (_, _) => true,
            getTicks: () => ticks));
        Assert.Equal("idle", aborted.Status);
        Assert.Equal("aborted", aborted.Outcome);
    }

    [Fact]
    public void ProcessMonitorDetail_ToleratesDuplicateSessionPids()
    {
        _monitor.SetSessionsForTest(
        [
            new AgentSession("claude:1", "claude", "first-session", "proj-a", "busy", null, 1000, 900, Pid: 4455),
            new AgentSession("antigravity:main", "antigravity", "Antigravity", "Antigravity", "idle", null, 1000, 900, Pid: 4455),
        ]);
        var procs = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, _monitor);
        var detail = procs.Detail("phonebox-nonexistent-process-name");
        Assert.Empty(detail.Instances);
    }

    [Fact]
    public void ChatReadersExtractLastPromptAndReplySafelyAndTruncateLongText()
    {
        var claudeProjects = Path.Combine(_root, ".claude", "projects", "work");
        Directory.CreateDirectory(claudeProjects);
        var longPrompt = new string('p', 1700);
        File.WriteAllText(Path.Combine(claudeProjects, "sess-1.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "user", message = new { content = longPrompt } }),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "All checks green." } } } })));

        var (claudePrompt, claudeReply) = SessionMonitor.ReadClaudeChat(_root, "sess-1");
        Assert.NotNull(claudePrompt);
        Assert.Equal(1501, claudePrompt!.Length);
        Assert.EndsWith("…", claudePrompt);
        Assert.Equal("All checks green.", claudeReply);
        Assert.Equal((null, null), SessionMonitor.ReadClaudeChat(_root, "../etc/passwd"));

        var codexFile = Rollout();
        File.AppendAllText(codexFile,
            Line("event_msg", new { type = "user_message", message = "Fix the build warning" }) +
            Line("event_msg", new { type = "agent_message", message = "Updated the nullable annotation." }));
        var codexHome = Path.Combine(_root, "codex-home");
        var codexSessDir = Path.Combine(codexHome, ".codex", "sessions", "2026", "11", "13");
        Directory.CreateDirectory(codexSessDir);
        File.Copy(codexFile, Path.Combine(codexSessDir, "rollout-test-1.jsonl"));
        var (cPrompt, cReply) = SessionMonitor.ReadCodexChat(codexHome, "rollout-test-1.jsonl");
        Assert.Equal("Fix the build warning", cPrompt);
        Assert.Equal("Updated the nullable annotation.", cReply);
        Assert.Equal((null, null), SessionMonitor.ReadCodexChat(codexHome, "../*.jsonl"));

        var agyLogDir = Path.Combine(_root, ".gemini", "antigravity", "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(agyLogDir);
        File.WriteAllText(Path.Combine(agyLogDir, "transcript.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "USER_INPUT", content = "Open PRs for remaining features" }),
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", content = "Prepared PR branch cleanly." })));
        var (agyPrompt, agyReply) = SessionMonitor.ReadAntigravityChat(_root);
        Assert.Equal("Open PRs for remaining features", agyPrompt);
        Assert.Equal("Prepared PR branch cleanly.", agyReply);
    }

    [Fact]
    public void ClaudeChat_PrefersLastPromptAndSkipsMetaCommandAndInterruptedEntries()
    {
        var claudeProjects = Path.Combine(_root, ".claude", "projects", "meta-test");
        Directory.CreateDirectory(claudeProjects);

        // When no last-prompt entry exists, skips isMeta, [Request interrupted by user], <command-*, <local-command-*, <system-reminder>.
        File.WriteAllText(Path.Combine(claudeProjects, "sess-meta.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "user", message = new { content = "Real user prompt" } }),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "Clean assistant reply" } } } }),
            JsonSerializer.Serialize(new { type = "user", isMeta = true, message = new { content = "Hidden meta line" } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "<command-name>/clear</command-name>" } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "<local-command-stdout>Set model</local-command-stdout>" } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "<system-reminder>Context note</system-reminder>" } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "[Request interrupted by user]" } }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "[Request interrupted by user for tool use]" } })));

        var (p1, r1) = SessionMonitor.ReadClaudeChat(_root, "sess-meta");
        Assert.Equal("Real user prompt", p1);
        Assert.Equal("Clean assistant reply", r1);

        // Explicit type == "last-prompt" wins even when generic trailing user entries follow it.
        File.WriteAllText(Path.Combine(claudeProjects, "sess-lp.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new { type = "user", message = new { content = "Earlier prompt" } }),
            JsonSerializer.Serialize(new { type = "assistant", message = new { content = "Reply text" } }),
            JsonSerializer.Serialize(new { type = "last-prompt", lastPrompt = "Explicit last prompt" }),
            JsonSerializer.Serialize(new { type = "user", message = new { content = "Trailing generic user line" } })));

        var (p2, r2) = SessionMonitor.ReadClaudeChat(_root, "sess-lp");
        Assert.Equal("Explicit last prompt", p2);
        Assert.Equal("Reply text", r2);
    }

    [Fact]
    public void AntigravityChat_ChecksSessionIdAndRejectsStaleBrainWhenConversationsPbIsNewer()
    {
        var agyDir = Path.Combine(_root, ".gemini", "antigravity");
        var brain1 = Path.Combine(agyDir, "brain", "conv-1", ".system_generated", "logs");
        var convDir = Path.Combine(agyDir, "conversations");
        Directory.CreateDirectory(brain1);
        Directory.CreateDirectory(convDir);

        var t1 = Path.Combine(brain1, "transcript.jsonl");
        File.WriteAllText(t1, string.Join("\n",
            JsonSerializer.Serialize(new { type = "USER_INPUT", content = "Old conversation prompt" }),
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", content = "Old conversation reply" })));
        File.SetLastWriteTimeUtc(t1, DateTime.UtcNow.AddMinutes(-2));

        // Explicit session ID matches only conv-1 and rejects mismatched or unsafe IDs.
        Assert.Equal(("Old conversation prompt", "Old conversation reply"), SessionMonitor.ReadAntigravityChat(_root, "antigravity:conv-1"));
        Assert.Equal((null, null), SessionMonitor.ReadAntigravityChat(_root, "conv-2"));
        Assert.Equal((null, null), SessionMonitor.ReadAntigravityChat(_root, "../conv-1"));

        // A newer opaque conversations/*.pb file for another conversation prevents returning stale brain/conv-1 messages.
        var pb2 = Path.Combine(convDir, "conv-2.pb");
        File.WriteAllBytes(pb2, [1, 2, 3]);
        File.SetLastWriteTimeUtc(pb2, DateTime.UtcNow);
        Assert.Equal((null, null), SessionMonitor.ReadAntigravityChat(_root, "antigravity:main"));
        Assert.Equal((null, null), SessionMonitor.ReadAntigravityChat(_root));

        // Once conv-2's brain transcript catches up, main resolves to conv-2.
        var brain2 = Path.Combine(agyDir, "brain", "conv-2", ".system_generated", "logs");
        Directory.CreateDirectory(brain2);
        var t2 = Path.Combine(brain2, "transcript.jsonl");
        File.WriteAllText(t2, string.Join("\n",
            JsonSerializer.Serialize(new { type = "USER_INPUT", content = "New conversation prompt" }),
            JsonSerializer.Serialize(new { type = "PLANNER_RESPONSE", content = "New conversation reply" })));
        File.SetLastWriteTimeUtc(t2, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(("New conversation prompt", "New conversation reply"), SessionMonitor.ReadAntigravityChat(_root, "antigravity:main"));
    }

    [Fact]
    public void ChatReaders_CacheTranscriptPathWithTtlAndFileExistsInvalidation()
    {
        var projectsDir = Path.Combine(_root, ".claude", "projects", "cache-proj");
        Directory.CreateDirectory(projectsDir);
        var path = Path.Combine(projectsDir, "sess-cache.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new { type = "user", message = new { content = "Cached prompt" } }));

        long now = 1_000_000;
        var scans = 0;
        var (p1, _) = SessionMonitor.ReadClaudeChat(_root, "sess-cache", () => now, () => scans++);
        Assert.Equal("Cached prompt", p1);
        Assert.Equal(1, scans);

        // Within 5-minute TTL, cached path is reused without scanning directories.
        now += 60_000;
        var (p2, _) = SessionMonitor.ReadClaudeChat(_root, "sess-cache", () => now, () => scans++);
        Assert.Equal("Cached prompt", p2);
        Assert.Equal(1, scans);

        // If the cached file disappears, File.Exists fails and forces a rescan.
        File.Delete(path);
        now += 10_000;
        var (p3, _) = SessionMonitor.ReadClaudeChat(_root, "sess-cache", () => now, () => scans++);
        Assert.Null(p3);
        Assert.Equal(2, scans);

        // Re-creating and advancing past the 5-minute TTL forces a fresh scan after expiration.
        File.WriteAllText(path, JsonSerializer.Serialize(new { type = "user", message = new { content = "Fresh after TTL" } }));
        var (p4, _) = SessionMonitor.ReadClaudeChat(_root, "sess-cache", () => now, () => scans++);
        Assert.Equal("Fresh after TTL", p4);
        Assert.Equal(3, scans);

        now += 5 * 60_000L;
        var (p5, _) = SessionMonitor.ReadClaudeChat(_root, "sess-cache", () => now, () => scans++);
        Assert.Equal("Fresh after TTL", p5);
        Assert.Equal(4, scans);

        // Bounded cache handles >128 entries cleanly without throwing or unbounded growth.
        for (var i = 0; i < 140; i++)
        {
            var id = $"sess-bulk-{i}";
            File.WriteAllText(Path.Combine(projectsDir, id + ".jsonl"),
                JsonSerializer.Serialize(new { type = "user", message = new { content = "bulk " + i } }));
            var (bp, _) = SessionMonitor.ReadClaudeChat(_root, id, () => now + i);
            Assert.Equal("bulk " + i, bp);
        }
    }

    [Fact]
    public void TruncateMessage_DoesNotSplitSurrogatePairs()
    {
        // High surrogate at index 1499 + low surrogate at index 1500 -> steps back to 1499 before appending ellipsis.
        var splitInput = new string('a', 1499) + "😀";
        var splitResult = SessionMonitor.TruncateMessage(splitInput);
        Assert.NotNull(splitResult);
        Assert.Equal(1500, splitResult!.Length);
        Assert.EndsWith("…", splitResult);
        Assert.False(char.IsHighSurrogate(splitResult[^2]));

        // Full surrogate pair fits within 1500 chars (indices 1498..1499) -> keeps the emoji intact.
        var intactInput = new string('a', 1498) + "😀" + "tail";
        var intactResult = SessionMonitor.TruncateMessage(intactInput);
        Assert.NotNull(intactResult);
        Assert.Equal(1501, intactResult!.Length);
        Assert.EndsWith("😀…", intactResult);
    }

    [Fact]
    public void AllowSessionMessages_IsOffByDefaultAndLanEndpointIsBlocked()
    {
        Directory.CreateDirectory(_root);
        var cfgPath = Path.Combine(_root, "config.json");
        File.WriteAllText(cfgPath, "{}");
        Assert.False(new ConfigStore(cfgPath).Get().AllowSessionMessages());

        File.WriteAllText(cfgPath, """{"pages":[{"id":"w","title":"Work","enabled":true,"widgets":[{"type":"sessions","options":{}}]}]}""");
        Assert.False(new ConfigStore(cfgPath).Get().AllowSessionMessages());

        File.WriteAllText(cfgPath, """{"pages":[{"id":"home","title":"Home","enabled":true,"widgets":[]},{"id":"w","title":"Work","enabled":false,"widgets":[{"type":"sessions","options":{"showMessages":true}}]}]}""");
        Assert.False(new ConfigStore(cfgPath).Get().AllowSessionMessages());

        File.WriteAllText(cfgPath, """{"pages":[{"id":"w","title":"Work","enabled":true,"widgets":[{"type":"sessions","options":{"showMessages":true}}]}]}""");
        Assert.True(new ConfigStore(cfgPath).Get().AllowSessionMessages());

        File.WriteAllText(cfgPath, """{"pages":[{"id":"w","title":"Work","enabled":true,"widgets":[{"type":"stack","options":{"widgets":[{"type":"sessions","enabled":true,"options":{"showMessages":true}}]}}]}]}""");
        Assert.True(new ConfigStore(cfgPath).Get().AllowSessionMessages());

        Assert.False(Lan.AllowedOverLan(new PathString("/api/sessions/detail")));
    }

    [Fact]
    public void ChatReaders_PreserveAllMultiBlockClaudeAndCodexMessagesInOrder()
    {
        var claudeProjects = Path.Combine(_root, ".claude", "projects", "multi-block");
        Directory.CreateDirectory(claudeProjects);
        File.WriteAllText(Path.Combine(claudeProjects, "sess-multi.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new
            {
                type = "user",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "text", text = "Fix authentication." },
                        new { type = "text", text = "" },
                        new { type = "image", source = "ignored" },
                        new { type = "text", text = "Keep the API unchanged." },
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "hidden" },
                        new { type = "text", text = "Updated token validation." },
                        new { type = "text", text = "All endpoint signatures remain unchanged." },
                    }
                }
            })));

        var (claudePrompt, claudeReply) = SessionMonitor.ReadClaudeChat(_root, "sess-multi");
        Assert.Equal("Fix authentication.\nKeep the API unchanged.", claudePrompt);
        Assert.Equal("Updated token validation.\nAll endpoint signatures remain unchanged.", claudeReply);

        var codexSessDir = Path.Combine(_root, "codex-multi", ".codex", "sessions", "2026", "11", "16");
        Directory.CreateDirectory(codexSessDir);
        File.WriteAllText(Path.Combine(codexSessDir, "rollout-multi-1.jsonl"), string.Join("\n",
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                timestamp = DateTimeOffset.UtcNow,
                payload = new
                {
                    type = "message",
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "Part one of prompt." },
                        new { type = "input_text", text = "" },
                        new { type = "text", text = "Part two of prompt." },
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                timestamp = DateTimeOffset.UtcNow,
                payload = new
                {
                    type = "message",
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "output_text", text = "First assistant paragraph." },
                        new { type = "text", text = "Second assistant paragraph." },
                    }
                }
            })));

        var (codexPrompt, codexReply) = SessionMonitor.ReadCodexChat(Path.Combine(_root, "codex-multi"), "rollout-multi-1.jsonl");
        Assert.Equal("Part one of prompt.\nPart two of prompt.", codexPrompt);
        Assert.Equal("First assistant paragraph.\nSecond assistant paragraph.", codexReply);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
