using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class OpenClawSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-openclaw-" + Guid.NewGuid().ToString("N"));
    private readonly string _clawDir;

    public OpenClawSessionTests()
    {
        Directory.CreateDirectory(_root);
        _clawDir = Path.Combine(_root, ".openclaw");
        Directory.CreateDirectory(_clawDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private (SessionMonitor Monitor, string ConfigPath) MonitorWithConfig(string configJson)
    {
        var cfgPath = Path.Combine(_root, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(cfgPath, configJson);
        return (new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(cfgPath)), cfgPath);
    }

    private SessionMonitor Monitor(string configJson) => MonitorWithConfig(configJson).Monitor;

    private SessionMonitor OptedInMonitor() =>
        Monitor("""{"pages":[{"id":"work","enabled":true,"widgets":[{"id":"s1","type":"sessions","options":{"showOpenClaw":true}}]}]}""");

    private static DateTime CurrentProcessStartUtc() => Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private void WriteGatewayPid(int pid, DateTime writeUtc, string? stateDir = null)
    {
        var dir = stateDir ?? _clawDir;
        Directory.CreateDirectory(dir);
        var pidPath = Path.Combine(dir, "gateway.pid");
        File.WriteAllText(pidPath, pid.ToString());
        File.SetLastWriteTimeUtc(pidPath, writeUtc);
    }

    [Fact]
    public void CatalogIncludesOptInShowOpenClawOptionDefaultingToFalse()
    {
        using var doc = JsonDocument.Parse(Catalog.WidgetsJson);
        var sessionsWidget = doc.RootElement.EnumerateArray().Single(w => w.GetProperty("type").GetString() == "sessions");
        var opt = sessionsWidget.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("key").GetString() == "showOpenClaw");
        Assert.Equal("bool", opt.GetProperty("type").GetString());
        Assert.False(opt.GetProperty("default").GetBoolean());
    }

    [Fact]
    public void OptInGateBlocksOpenClawUnlessEnabledOnVisiblePageOrStack()
    {
        var startUtc = CurrentProcessStartUtc();
        WriteGatewayPid(Environment.ProcessId, startUtc);

        // Null config -> off
        var noCfgMonitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance);
        Assert.False(noCfgMonitor.IsOpenClawEnabled());
        Assert.Empty(noCfgMonitor.ReadOpenClaw(_root, listProcesses: () => []));

        // Default sessions widget without showOpenClaw -> off
        var defaultMonitor = Monitor("""{"pages":[{"id":"work","enabled":true,"widgets":[{"id":"s1","type":"sessions"}]}]}""");
        Assert.False(defaultMonitor.IsOpenClawEnabled());
        Assert.Empty(defaultMonitor.ReadOpenClaw(_root, listProcesses: () => []));

        // Explicit false -> off
        var falseMonitor = Monitor("""{"pages":[{"id":"work","enabled":true,"widgets":[{"id":"s1","type":"sessions","options":{"showOpenClaw":false}}]}]}""");
        Assert.False(falseMonitor.IsOpenClawEnabled());
        Assert.Empty(falseMonitor.ReadOpenClaw(_root, listProcesses: () => []));

        // Disabled page -> off
        var hiddenPageMonitor = Monitor("""
            {"pages":[
              {"id":"home","enabled":true,"widgets":[]},
              {"id":"work","enabled":false,"widgets":[{"id":"s1","type":"sessions","options":{"showOpenClaw":true}}]}
            ]}
            """);
        Assert.False(hiddenPageMonitor.IsOpenClawEnabled());
        Assert.Empty(hiddenPageMonitor.ReadOpenClaw(_root, listProcesses: () => []));

        // Disabled stack child -> off
        var disabledChildMonitor = Monitor("""
            {"pages":[{"id":"work","enabled":true,"widgets":[
              {"id":"st","type":"stack","options":{"widgets":[{"id":"c1","type":"sessions","enabled":false,"options":{"showOpenClaw":true}}]}}
            ]}]}
            """);
        Assert.False(disabledChildMonitor.IsOpenClawEnabled());
        Assert.Empty(disabledChildMonitor.ReadOpenClaw(_root, listProcesses: () => []));

        // Enabled stack child on enabled page -> on
        var enabledChildMonitor = Monitor("""
            {"pages":[{"id":"work","enabled":true,"widgets":[
              {"id":"st","type":"stack","options":{"widgets":[{"id":"c1","type":"sessions","enabled":true,"options":{"showOpenClaw":true}}]}}
            ]}]}
            """);
        Assert.True(enabledChildMonitor.IsOpenClawEnabled());
        var childSession = Assert.Single(enabledChildMonitor.ReadOpenClaw(_root, listProcesses: () => []));
        Assert.Equal("openclaw", childSession.Tool);

        // Enabled top-level sessions widget -> on (distinct config file from defaultMonitor)
        var (enabledMonitor, enabledCfgPath) = MonitorWithConfig(
            """{"pages":[{"id":"work","enabled":true,"widgets":[{"id":"s1","type":"sessions","options":{"showOpenClaw":true}}]}]}""");
        Assert.True(enabledMonitor.IsOpenClawEnabled());
        var session = Assert.Single(enabledMonitor.ReadOpenClaw(_root, listProcesses: () => []));
        Assert.Equal("openclaw:gateway", session.Id);
        Assert.Equal("OpenClaw", session.Name);
        Assert.Equal("Gateway", session.Project);

        // ToPublic exposes showOpenClaw to the panel
        using var pubDoc = JsonDocument.Parse(JsonSerializer.Serialize(new ConfigStore(enabledCfgPath).Get().ToPublic(), ConfigStore.WriteOpts));
        var optVal = pubDoc.RootElement.GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("options").GetProperty("showOpenClaw").GetBoolean();
        Assert.True(optVal);

        // defaultMonitor remains opted out after enabledMonitor was created, and PollOnce(_root) stays isolated from real HOME
        Assert.False(defaultMonitor.IsOpenClawEnabled());
        Assert.DoesNotContain(defaultMonitor.PollOnce(_root), s => s.Tool == "openclaw");
    }

    [Fact]
    public void NonSessionFilesUnderOpenClawDoNotMarkGatewayBusy()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var procStart = now.AddHours(-1);
        var procStartMs = new DateTimeOffset(procStart).ToUnixTimeMilliseconds();
        WriteGatewayPid(5566, procStart);

        // Touch config, logs, credentials, state, and agent sqlite right now
        File.WriteAllText(Path.Combine(_clawDir, "openclaw.json"), "{\"secret\":\"token\"}");
        Directory.CreateDirectory(Path.Combine(_clawDir, "logs"));
        File.WriteAllText(Path.Combine(_clawDir, "logs", "gateway.log"), "heartbeat\n");
        Directory.CreateDirectory(Path.Combine(_clawDir, "credentials"));
        File.WriteAllText(Path.Combine(_clawDir, "credentials", "auth.json"), "{\"key\":\"abc\"}");
        Directory.CreateDirectory(Path.Combine(_clawDir, "state"));
        File.WriteAllText(Path.Combine(_clawDir, "state", "openclaw.sqlite"), "sqlite");
        Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "main", "agent"));
        File.WriteAllText(Path.Combine(_clawDir, "agents", "main", "agent", "openclaw-agent.sqlite"), "sqlite");

        // Touch .lock and .deleted.* inside sessions/ right now, while real .jsonl is old
        var sessionsDir = Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "main", "sessions")).FullName;
        var oldSession = Path.Combine(sessionsDir, "sess-old.jsonl");
        File.WriteAllText(oldSession, "{\"type\":\"session\"}\n");
        File.SetLastWriteTimeUtc(oldSession, now.AddMinutes(-15));
        File.WriteAllText(Path.Combine(sessionsDir, "sess-old.jsonl.lock"), "{\"pid\":1}");
        File.WriteAllText(Path.Combine(sessionsDir, "sess-del.jsonl.deleted.2026-11-16"), "{}");
        File.WriteAllText(Path.Combine(sessionsDir, "sess-del.deleted.jsonl"), "{}");

        var monitor = OptedInMonitor();
        var session = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 5566,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now,
            getTicksMs: () => 1_000));
        Assert.Equal("idle", session.Status);
        Assert.Equal(new DateTimeOffset(now.AddMinutes(-15)).ToUnixTimeMilliseconds(), session.Since);
    }

    [Fact]
    public void RecentSessionJsonlMarksBusyAndOldWriteMarksIdleWithCachedDiscovery()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var procStartMs = new DateTimeOffset(now.AddHours(-1)).ToUnixTimeMilliseconds();
        WriteGatewayPid(5566, now.AddHours(-1));

        var sessionsDir = Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "main", "sessions")).FullName;
        var jsonl = Path.Combine(sessionsDir, "sess-1.jsonl");
        File.WriteAllText(jsonl, "{\"type\":\"session\"}\n");
        File.SetLastWriteTimeUtc(jsonl, now.AddSeconds(-20));

        var monitor = OptedInMonitor();
        long ticks = 1_000;

        // First poll -> busy
        var s1 = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 5566,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now,
            getTicksMs: () => ticks));
        Assert.Equal("busy", s1.Status);
        Assert.Equal(new DateTimeOffset(now.AddSeconds(-20)).ToUnixTimeMilliseconds(), s1.Since);

        // 2s later: file updated within 30s cache window -> still picks up new mtime immediately
        ticks = 3_000;
        var now2 = now.AddSeconds(2);
        File.SetLastWriteTimeUtc(jsonl, now2);
        var s2 = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 5566,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now2,
            getTicksMs: () => ticks));
        Assert.Equal("busy", s2.Status);
        Assert.Equal(new DateTimeOffset(now2).ToUnixTimeMilliseconds(), s2.Since);

        // 3 minutes pass with no writes -> idle
        var now3 = now2.AddMinutes(3);
        ticks = 183_000;
        var s3 = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 5566,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now3,
            getTicksMs: () => ticks));
        Assert.Equal("idle", s3.Status);
        Assert.Equal(new DateTimeOffset(now2).ToUnixTimeMilliseconds(), s3.Since);
    }

    [Fact]
    public void ProcessDiscoveryRespectsThirtySecondThrottleAndCachesVerifiedPid()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var startMs = new DateTimeOffset(now.AddMinutes(-10)).ToUnixTimeMilliseconds();
        var monitor = OptedInMonitor();

        var scanCount = 0;
        IReadOnlyList<SessionMonitor.OpenClawProcessInfo> currentProcesses = [];
        IReadOnlyList<SessionMonitor.OpenClawProcessInfo> Scan()
        {
            scanCount++;
            return currentProcesses;
        }

        // First call at t=1000 triggers 1 scan (empty)
        Assert.Empty(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 202, getProcessStartMs: _ => startMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 1_000));
        Assert.Equal(1, scanCount);

        // Gateway appears at t=15000 and t=30999, still within 30s throttle -> no extra scan
        currentProcesses = [new(202, "node", "/usr/local/bin/node /opt/openclaw/openclaw.mjs gateway --port 18789", startMs)];
        Assert.Empty(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 202, getProcessStartMs: _ => startMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 15_000));
        Assert.Empty(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 202, getProcessStartMs: _ => startMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 30_999));
        Assert.Equal(1, scanCount);

        // At t=31000 (30s elapsed) -> 2nd scan runs and finds gateway, caching (pid=202, startMs)
        var s1 = Assert.Single(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 202, getProcessStartMs: _ => startMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 31_000));
        Assert.Equal(2, scanCount);
        Assert.Equal(202, s1.Pid);

        // Subsequent polls hit the (pid, startMs) cache without re-scanning
        var s2 = Assert.Single(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 202, getProcessStartMs: _ => startMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 75_000));
        Assert.Equal(2, scanCount);
        Assert.Equal(202, s2.Pid);
    }

    [Fact]
    public void VerifiedIdentityCachePreventsRepeatProcessChecksForHeartbeatRenewedLock()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var procStart = now.AddHours(-2);
        var procStartMs = new DateTimeOffset(procStart).ToUnixTimeMilliseconds();

        // gateway.pid has no createdAt and its mtime was renewed 2 hours after process start (>120s)
        WriteGatewayPid(6060, now);

        var monitor = OptedInMonitor();
        var identityChecks = 0;
        var procList = new SessionMonitor.OpenClawProcessInfo[]
        {
            new(6060, "node", "node /opt/openclaw/openclaw.mjs gateway --port 18789", procStartMs),
        };
        IReadOnlyList<SessionMonitor.OpenClawProcessInfo> Scan()
        {
            identityChecks++;
            return procList;
        }

        // First poll verifies process identity once
        var s1 = Assert.Single(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 6060, getProcessStartMs: _ => procStartMs, listProcesses: Scan, utcNow: () => now, getTicksMs: () => 1_000));
        Assert.Equal(6060, s1.Pid);
        Assert.Equal(1, identityChecks);

        // Repeated polls with lock heartbeat updating mtime reuse the verified (pid, startMs) pair without re-checking
        File.SetLastWriteTimeUtc(Path.Combine(_clawDir, "gateway.pid"), now.AddSeconds(2));
        Assert.Single(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 6060, getProcessStartMs: _ => procStartMs, listProcesses: Scan, utcNow: () => now.AddSeconds(2), getTicksMs: () => 3_000));
        File.SetLastWriteTimeUtc(Path.Combine(_clawDir, "gateway.pid"), now.AddMinutes(1));
        Assert.Single(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 6060, getProcessStartMs: _ => procStartMs, listProcesses: Scan, utcNow: () => now.AddMinutes(1), getTicksMs: () => 61_000));
        Assert.Equal(1, identityChecks);

        // If PID is recycled by an unrelated process with a different start time, identity is re-checked and rejected
        var recycledStartMs = procStartMs + 3_000_000;
        File.SetLastWriteTimeUtc(Path.Combine(_clawDir, "gateway.pid"), new DateTimeOffset(DateTime.UnixEpoch).AddMilliseconds(recycledStartMs + 300_000).UtcDateTime);
        procList = [new(6060, "bash", "bash", recycledStartMs)];
        Assert.Empty(monitor.ReadOpenClaw(_root, isAlive: pid => pid == 6060, getProcessStartMs: _ => recycledStartMs, listProcesses: Scan, utcNow: () => now.AddMinutes(2), getTicksMs: () => 63_000));
        Assert.Equal(2, identityChecks);
    }

    [Fact]
    public void MissingStartTimeIsNeverCachedAndReusedPidIsRejectedImmediately()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var monitor = OptedInMonitor();

        // Case A: gateway process discovered with null StartMs -> returned on scan tick, NEVER cached
        var scanCalls = 0;
        IReadOnlyList<SessionMonitor.OpenClawProcessInfo> ScanNoStart()
        {
            scanCalls++;
            return [new(7001, "node", "node /opt/openclaw/openclaw.mjs gateway", null)];
        }

        var found = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 7001,
            getProcessStartMs: _ => null,
            listProcesses: ScanNoStart,
            utcNow: () => now,
            getTicksMs: () => 1_000));
        Assert.Equal(7001, found.Pid);
        Assert.Equal(0, found.StartedAt);
        Assert.Equal(1, scanCalls);

        // 2s later within 30s window: not cached because startMs was null, and throttle prevents rescan
        Assert.Empty(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 7001,
            getProcessStartMs: _ => null,
            listProcesses: ScanNoStart,
            utcNow: () => now.AddSeconds(2),
            getTicksMs: () => 3_000));
        Assert.Equal(1, scanCalls);

        // Case B: gateway discovered with valid startMs -> cached; if startMs changes or becomes null (PID reuse), rejected immediately
        var monitor2 = OptedInMonitor();
        var validStart = new DateTimeOffset(now.AddMinutes(-5)).ToUnixTimeMilliseconds();
        long? currentStart = validStart;
        IReadOnlyList<SessionMonitor.OpenClawProcessInfo> procs2 =
        [
            new(7002, "node", "node /opt/openclaw/openclaw.mjs gateway", validStart),
        ];

        Assert.Single(monitor2.ReadOpenClaw(
            _root, isAlive: pid => pid == 7002, getProcessStartMs: _ => currentStart,
            listProcesses: () => procs2, utcNow: () => now, getTicksMs: () => 1_000));

        // PID 7002 now belongs to a newer process (startMs shifted)
        currentStart = validStart + 10_000;
        Assert.Empty(monitor2.ReadOpenClaw(
            _root, isAlive: pid => pid == 7002, getProcessStartMs: _ => currentStart,
            listProcesses: () => procs2, utcNow: () => now.AddSeconds(2), getTicksMs: () => 3_000));

        // Even after cache cleared, if startMs becomes null on cached PID it is rejected immediately
        var monitor3 = OptedInMonitor();
        long? start3 = validStart;
        Assert.Single(monitor3.ReadOpenClaw(
            _root, isAlive: pid => pid == 7002, getProcessStartMs: _ => start3,
            listProcesses: () => procs2, utcNow: () => now, getTicksMs: () => 1_000));
        start3 = null;
        Assert.Empty(monitor3.ReadOpenClaw(
            _root, isAlive: pid => pid == 7002, getProcessStartMs: _ => start3,
            listProcesses: () => procs2, utcNow: () => now.AddSeconds(2), getTicksMs: () => 3_000));
    }

    [Fact]
    public void RecycledOrStalePidAndNonGatewayLockOrPidFilesAreRejected()
    {
        var monitor = OptedInMonitor();

        // 1. Live OS PID (current test process) in a backdated pid file -> rejected by SameStart
        WriteGatewayPid(Environment.ProcessId, DateTime.UtcNow.AddHours(-2));
        Assert.Empty(monitor.ReadOpenClaw(_root, listProcesses: () => []));

        // 2. Live OS PID in a backdated OpenClaw JSON lock file -> rejected by SameStart
        File.Delete(Path.Combine(_clawDir, "gateway.pid"));
        var tmpLockDir = Directory.CreateDirectory(Path.Combine(_clawDir, "tmp", "openclaw-501")).FullName;
        var lockPath = Path.Combine(tmpLockDir, "gateway.state.lock");
        var staleTime = DateTime.UtcNow.AddHours(-3);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            role = "gateway",
            createdAt = staleTime.ToString("O"),
        }));
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow);
        Assert.Empty(monitor.ReadOpenClaw(_root, listProcesses: () => [], getTicksMs: () => 40_000));

        // 3. Mocked recycled PID whose start time is after lock write time -> rejected
        File.Delete(lockPath);
        var lockTime = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        var lockWriteMs = new DateTimeOffset(lockTime).ToUnixTimeMilliseconds();
        WriteGatewayPid(7788, lockTime);
        Assert.Empty(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 7788,
            getProcessStartMs: _ => lockWriteMs + 60_000,
            listProcesses: () => [],
            getTicksMs: () => 80_000));
        File.Delete(Path.Combine(_clawDir, "gateway.pid"));

        // 4. Unrelated helper.pid in ~/.openclaw -> ignored
        var helperPid = Path.Combine(_clawDir, "helper.pid");
        File.WriteAllText(helperPid, Environment.ProcessId.ToString());
        File.SetLastWriteTimeUtc(helperPid, CurrentProcessStartUtc());
        Assert.Empty(monitor.ReadOpenClaw(_root, listProcesses: () => [], getTicksMs: () => 120_000));
        File.Delete(helperPid);

        // 5. state.*.lock without role property -> rejected
        var noRoleLock = Path.Combine(tmpLockDir, "state.norole.lock");
        File.WriteAllText(noRoleLock, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            createdAt = CurrentProcessStartUtc().ToString("O"),
        }));
        Assert.Empty(monitor.ReadOpenClaw(_root, listProcesses: () => [], getTicksMs: () => 160_000));

        // 6. state.*.lock with non-gateway role -> rejected
        File.WriteAllText(noRoleLock, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            role = "sqlite-maintenance",
            createdAt = CurrentProcessStartUtc().ToString("O"),
        }));
        Assert.Empty(monitor.ReadOpenClaw(_root, listProcesses: () => [], getTicksMs: () => 200_000));

        // 7. state.*.lock with explicit role == "gateway" -> accepted
        File.WriteAllText(noRoleLock, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            role = "gateway",
            createdAt = CurrentProcessStartUtc().ToString("O"),
        }));
        var matched = Assert.Single(monitor.ReadOpenClaw(_root, listProcesses: () => [], getTicksMs: () => 240_000));
        Assert.Equal(Environment.ProcessId, matched.Pid);
    }

    [Fact]
    public void DiscoversGatewayFromOpenClawStateDirAndWindowsLocksDirectory()
    {
        var now = new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);
        var procStart = now.AddMinutes(-10);
        var procStartMs = new DateTimeOffset(procStart).ToUnixTimeMilliseconds();

        // 1. $OPENCLAW_STATE_DIR with gateway.lock and active session
        var customStateDir = Directory.CreateDirectory(Path.Combine(_root, "custom-state")).FullName;
        var stateLock = Path.Combine(customStateDir, "gateway.lock");
        File.WriteAllText(stateLock, JsonSerializer.Serialize(new
        {
            pid = 8101,
            role = "gateway",
            createdAt = procStart.ToString("O"),
        }));
        var customSessions = Directory.CreateDirectory(Path.Combine(customStateDir, "agents", "main", "sessions")).FullName;
        var customJsonl = Path.Combine(customSessions, "s1.jsonl");
        File.WriteAllText(customJsonl, "{}\n");
        File.SetLastWriteTimeUtc(customJsonl, now.AddSeconds(-15));

        var monitor1 = OptedInMonitor();
        var s1 = Assert.Single(monitor1.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 8101,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now,
            getTicksMs: () => 1_000,
            stateDirOverride: customStateDir));
        Assert.Equal(8101, s1.Pid);
        Assert.Equal("busy", s1.Status);

        // 2. Windows-style %LOCALAPPDATA%\OpenClaw\locks\gateway*.lock
        var localAppData = Path.Combine(_root, "localappdata");
        var winLocksDir = Directory.CreateDirectory(Path.Combine(localAppData, "OpenClaw", "locks")).FullName;
        var winLock = Path.Combine(winLocksDir, "gateway.18789.lock");
        File.WriteAllText(winLock, JsonSerializer.Serialize(new
        {
            pid = 8102,
            createdAt = procStart.ToString("O"),
        }));

        var monitor2 = OptedInMonitor();
        var s2 = Assert.Single(monitor2.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 8102,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => now,
            getTicksMs: () => 1_000,
            localAppDataOverride: localAppData));
        Assert.Equal(8102, s2.Pid);
        Assert.Equal(procStartMs, s2.StartedAt);

        // 3. When process start time is unavailable, verified gateway with matching lock uses lock createdAt for StartedAt
        var monitor3 = OptedInMonitor();
        var s3 = Assert.Single(monitor3.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 8102,
            getProcessStartMs: _ => null,
            listProcesses: () => [new(8102, "node", "node C:\\openclaw\\openclaw.mjs gateway", null)],
            utcNow: () => now,
            getTicksMs: () => 1_000,
            localAppDataOverride: localAppData));
        Assert.Equal(procStartMs, s3.StartedAt);
    }

    [Fact]
    public void IncrementalPerFolderActivityRescanAndZeroStartedAtWithoutStartTimeOrLock()
    {
        var t0 = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var procStartMs = new DateTimeOffset(t0.AddMinutes(-20)).ToUnixTimeMilliseconds();
        WriteGatewayPid(9001, t0.AddMinutes(-20));

        var alphaSessions = Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "alpha", "sessions")).FullName;
        var betaSessions = Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "beta", "sessions")).FullName;

        var alphaFile = Path.Combine(alphaSessions, "a1.jsonl");
        File.WriteAllText(alphaFile, "{}\n");
        File.SetLastWriteTimeUtc(alphaFile, t0.AddMinutes(-10));
        Directory.SetLastWriteTimeUtc(alphaSessions, t0.AddMinutes(-10));

        var betaFile = Path.Combine(betaSessions, "b1.jsonl");
        File.WriteAllText(betaFile, "{}\n");
        File.SetLastWriteTimeUtc(betaFile, t0.AddMinutes(-30));
        Directory.SetLastWriteTimeUtc(betaSessions, t0.AddMinutes(-30));

        var enumerated = new List<string>();
        var monitor = OptedInMonitor();

        // Poll 1 enumerates both alpha and beta sessions folders
        var p1 = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 9001,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => t0,
            getTicksMs: () => 1_000,
            onEnumerateSessionsDir: dir => enumerated.Add(dir)));
        Assert.Equal("idle", p1.Status);
        Assert.Equal(2, enumerated.Count);
        Assert.Contains(alphaSessions, enumerated);
        Assert.Contains(betaSessions, enumerated);

        // Touch alpha/sessions only (simulating *.jsonl.lock churn while OpenClaw is active); beta/sessions stays untouched
        enumerated.Clear();
        var t1 = t0.AddSeconds(2);
        var alphaLock = Path.Combine(alphaSessions, "a1.jsonl.lock");
        File.WriteAllText(alphaLock, "{\"pid\":9001}");
        File.SetLastWriteTimeUtc(alphaFile, t1);
        Directory.SetLastWriteTimeUtc(alphaSessions, t1);

        var p2 = Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 9001,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => t1,
            getTicksMs: () => 3_000,
            onEnumerateSessionsDir: dir => enumerated.Add(dir)));
        Assert.Equal("busy", p2.Status);
        Assert.Equal(alphaSessions, Assert.Single(enumerated));

        // Adding a new agent folder enumerates only the new folder, not untouched beta/sessions
        enumerated.Clear();
        var t2 = t0.AddSeconds(4);
        var gammaSessions = Directory.CreateDirectory(Path.Combine(_clawDir, "agents", "gamma", "sessions")).FullName;
        Directory.SetLastWriteTimeUtc(gammaSessions, t2);
        Directory.SetLastWriteTimeUtc(Path.Combine(_clawDir, "agents"), t2);

        Assert.Single(monitor.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 9001,
            getProcessStartMs: _ => procStartMs,
            listProcesses: () => [],
            utcNow: () => t2,
            getTicksMs: () => 5_000,
            onEnumerateSessionsDir: dir => enumerated.Add(dir)));
        Assert.Equal(gammaSessions, Assert.Single(enumerated));

        // Without process start time or lock file, StartedAt is 0 (never oldest session file) and Since clamps >= StartedAt
        File.Delete(Path.Combine(_clawDir, "gateway.pid"));
        var monitor2 = OptedInMonitor();
        var procInfo = new SessionMonitor.OpenClawProcessInfo(9002, "node", "/usr/local/bin/node /opt/openclaw/openclaw.mjs gateway", null);
        var b1 = Assert.Single(monitor2.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 9002,
            getProcessStartMs: _ => null,
            listProcesses: () => [procInfo],
            utcNow: () => t1,
            getTicksMs: () => 1_000));
        Assert.Equal(0, b1.StartedAt);
        Assert.Equal(new DateTimeOffset(t1).ToUnixTimeMilliseconds(), b1.Since);

        // When only old session exists from before gateway started, Since clamps to StartedAt
        var gatewayStartedLater = new DateTimeOffset(t0.AddHours(2)).ToUnixTimeMilliseconds();
        WriteGatewayPid(9003, t0.AddHours(2));
        var monitor3 = OptedInMonitor();
        var c1 = Assert.Single(monitor3.ReadOpenClaw(
            _root,
            isAlive: pid => pid == 9003,
            getProcessStartMs: _ => gatewayStartedLater,
            listProcesses: () => [],
            utcNow: () => t0.AddHours(2).AddMinutes(1),
            getTicksMs: () => 1_000));
        Assert.Equal(gatewayStartedLater, c1.StartedAt);
        Assert.Equal(gatewayStartedLater, c1.Since);
    }

    [Fact]
    public void NodeAndGatewayProcessFallbackDetectsOpenClawAndIgnoresUnrelatedNode()
    {
        var now = new DateTime(2026, 11, 16, 14, 0, 0, DateTimeKind.Utc);
        var startMs = new DateTimeOffset(now.AddMinutes(-10)).ToUnixTimeMilliseconds();
        var monitor = OptedInMonitor();

        // Unrelated node and openclaw CLI commands are ignored
        var unrelated = new SessionMonitor.OpenClawProcessInfo[]
        {
            new(100, "node", "node /app/server.js", startMs),
            new(101, "openclaw", "openclaw status", startMs),
            new(102, "openclaw", "openclaw gateway status", startMs),
        };
        Assert.Empty(monitor.ReadOpenClaw(
            _root, isAlive: pid => pid is 100 or 101 or 102, getProcessStartMs: _ => startMs,
            listProcesses: () => unrelated, utcNow: () => now, getTicksMs: () => 1_000));

        // Node gateway process is detected once 30s discovery interval elapses
        var gatewayNode = new SessionMonitor.OpenClawProcessInfo[]
        {
            new(100, "node", "node /app/server.js", startMs),
            new(202, "node", "/usr/local/bin/node /Users/test/.openclaw/runtime/lib/node_modules/openclaw/openclaw.mjs gateway --port 18789", startMs),
        };
        var session = Assert.Single(monitor.ReadOpenClaw(
            _root, isAlive: pid => pid is 100 or 202, getProcessStartMs: _ => startMs,
            listProcesses: () => gatewayNode, utcNow: () => now, getTicksMs: () => 31_000));
        Assert.Equal(202, session.Pid);
        Assert.Equal(startMs, session.StartedAt);
    }

    [Fact]
    public void RuleEngineGenericSessionRulesIgnoreOpenClawUnlessExplicitlyMatched()
    {
        var cfgPath = Path.Combine(_root, $"rules-{Guid.NewGuid():N}.json");
        void WriteRules(string rulesJson)
        {
            File.WriteAllText(cfgPath, "{\"rules\":[" + rulesJson + "]}");
            File.SetLastWriteTimeUtc(cfgPath, DateTime.UtcNow.AddTicks(Random.Shared.Next(1, 1_000_000)));
        }

        WriteRules("");
        var cfg = new ConfigStore(cfgPath);
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_root, "data.json"));
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance, cfg);
        var engine = new RuleEngine(
            cfg, push, sessions,
            new MediaMonitor(NullLogger<MediaMonitor>.Instance),
            new SystemMonitor(NullLogger<SystemMonitor>.Instance),
            new PrivacyMonitor(NullLogger<PrivacyMonitor>.Instance),
            new CalendarMonitor(cfg, NullLogger<CalendarMonitor>.Instance),
            new ObsMonitor(cfg, NullLogger<ObsMonitor>.Instance),
            new QuotaMonitor(NullLogger<QuotaMonitor>.Instance),
            new GitHubMonitor(NullLogger<GitHubMonitor>.Instance),
            new BatteryMonitor(NullLogger<BatteryMonitor>.Instance),
            new PingMonitor(),
            new AudioControl(),
            NullLogger<RuleEngine>.Instance);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        sessions.SetSnapshotForTest([
            new AgentSession("openclaw:gateway", "openclaw", "OpenClaw", "Gateway", "busy", null, nowMs, nowMs, Pid: 9001),
        ]);

        // Empty Match on session-busy and session-waiting does NOT trigger for OpenClaw
        WriteRules("""
            {"id":"r-generic-busy","name":"Busy","when":"session-busy","then":"notify"},
            {"id":"r-generic-wait","name":"Wait","when":"session-waiting","then":"notify"}
            """);
        engine.Evaluate();
        Assert.DoesNotContain("r-generic-busy", engine.Active());
        Assert.DoesNotContain("r-generic-wait", engine.Active());

        // Explicit Match = "openclaw" or "gateway" DOES trigger
        WriteRules("""
            {"id":"r-openclaw","name":"OC","when":"session-busy","match":"openclaw","then":"notify"},
            {"id":"r-gateway","name":"GW","when":"session-busy","match":"gateway","then":"notify"}
            """);
        engine.Evaluate();
        Assert.Contains("r-openclaw", engine.Active());
        Assert.Contains("r-gateway", engine.Active());

        // Empty Match still triggers for coding sessions (Claude/Codex) alongside OpenClaw
        sessions.SetSnapshotForTest([
            new AgentSession("openclaw:gateway", "openclaw", "OpenClaw", "Gateway", "busy", null, nowMs, nowMs, Pid: 9001),
            new AgentSession("claude:1", "claude", "claude-app", "my-repo", "busy", null, nowMs, nowMs, Pid: 9002),
        ]);
        WriteRules("""{"id":"r-generic-busy","name":"Busy","when":"session-busy","then":"notify"}""");
        engine.Evaluate();
        Assert.Contains("r-generic-busy", engine.Active());
    }

    [Fact]
    public void ProcessCommandLineReadsCurrentProcessAcrossSingleAndBatchedCalls()
    {
        var selfPid = Environment.ProcessId;
        var single = ProcessCommandLine.Read(selfPid);
        Assert.False(string.IsNullOrWhiteSpace(single));

        var batchInput = Enumerable.Range(1, 70).Append(selfPid).ToArray();
        var many = ProcessCommandLine.ReadMany(batchInput);
        Assert.True(many.TryGetValue(selfPid, out var fromMany));
        Assert.Equal(single, fromMany);

        Assert.Null(ProcessCommandLine.Read(0));
        Assert.Null(ProcessCommandLine.Read(-1));
        Assert.Empty(ProcessCommandLine.ReadMany([]));
    }

    [Theory]
    [InlineData("openclaw-gateway", true)]
    [InlineData("/usr/local/bin/openclaw-gateway --port 18789", true)]
    [InlineData("/Users/x/.openclaw/runtime/current/bin/bun /Users/x/.openclaw/runtime/current/lib/node_modules/openclaw/openclaw.mjs gateway", true)]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Users\\x\\npm\\node_modules\\openclaw\\dist\\index.js\" gateway", true)]
    [InlineData("openclaw gateway", true)]
    [InlineData("openclaw gateway run", true)]
    [InlineData("openclaw gateway start", true)]
    [InlineData("openclaw gateway serve", true)]
    [InlineData("openclaw gateway --port 18789", true)]
    [InlineData("openclaw gateway --auth token --port 18789", true)]
    [InlineData("openclaw status", false)]
    [InlineData("openclaw gateway status", false)]
    [InlineData("openclaw gateway stop", false)]
    [InlineData("openclaw gateway restart", false)]
    [InlineData("openclaw gateway reload", false)]
    [InlineData("openclaw gateway install", false)]
    [InlineData("openclaw gateway uninstall", false)]
    [InlineData("openclaw gateway health", false)]
    [InlineData("openclaw gateway probe", false)]
    [InlineData("openclaw gateway call", false)]
    [InlineData("openclaw gateway logs", false)]
    [InlineData("openclaw gateway help", false)]
    [InlineData("openclaw gateway config", false)]
    [InlineData("openclaw gateway token", false)]
    [InlineData("openclaw gateway version", false)]
    [InlineData("openclaw gateway --port 18789 status", false)]
    [InlineData("node /work/my-app/server.js", false)]
    [InlineData("node /work/openclaw/scripts/test.js", false)]
    [InlineData("node server.js --label openclaw-gateway", false)]
    public void CommandLineClassifierMatchesOnlyOpenClawGateway(string cmd, bool expected)
    {
        Assert.Equal(expected, SessionMonitor.IsOpenClawGatewayCommandLine(cmd));
    }
}
