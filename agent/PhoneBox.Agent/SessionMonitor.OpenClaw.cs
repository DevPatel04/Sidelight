using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class SessionMonitor
{
    private const long OpenClawDiscoveryIntervalMs = 30_000;
    private const int OpenClawMaxLockBytes = 64 * 1024;

    internal readonly record struct OpenClawProcessInfo(int Pid, string Name, string? CommandLine, long? StartMs);
    private readonly record struct OpenClawGatewayMatch(int Pid, long? StartMs, long? LockTimeMs);

    private static readonly HashSet<string> OpenClawCliSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "stop", "restart", "reload", "install", "uninstall",
        "health", "probe", "call", "logs", "help", "config", "token", "version",
    };

    private static StringComparer OpenClawPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private string? _openClawScopeKey;
    private readonly Dictionary<string, DateTime> _openClawAgentsDirWrites = new(OpenClawPathComparer);
    private readonly Dictionary<string, string[]> _openClawAgentsSessionsDirs = new(OpenClawPathComparer);
    private readonly Dictionary<string, (DateTime DirWriteUtc, string[] CandidateFiles)> _openClawDirCache = new(OpenClawPathComparer);
    private long _openClawNextDiscovery = long.MinValue;

    private int? _openClawCachedPid;
    private long? _openClawCachedStartMs;
    private long _openClawNextProcDiscovery = long.MinValue;
    private (int Pid, long StartMs)? _openClawVerifiedIdentity;

    /// <summary>True when at least one enabled page (or enabled Smart Stack child) opts in to OpenClaw sessions.</summary>
    internal bool IsOpenClawEnabled() => config?.Get().ShowOpenClawSessions() == true;

    private void ResetOpenClawCache()
    {
        _openClawScopeKey = null;
        _openClawAgentsDirWrites.Clear();
        _openClawAgentsSessionsDirs.Clear();
        _openClawDirCache.Clear();
        _openClawNextDiscovery = long.MinValue;
        _openClawCachedPid = null;
        _openClawCachedStartMs = null;
        _openClawNextProcDiscovery = long.MinValue;
        _openClawVerifiedIdentity = null;
    }

    /// <summary>
    /// Reads the local OpenClaw gateway session when opted in. Gateway presence comes from verified lock/PID files
    /// or running gateway processes; activity comes exclusively from ~/.openclaw/agents/*/sessions/*.jsonl
    /// (and $OPENCLAW_STATE_DIR/agents/*/sessions/*.jsonl when configured).
    /// </summary>
    internal IEnumerable<AgentSession> ReadOpenClaw(
        string? homeDir = null,
        Func<int, bool>? isAlive = null,
        Func<int, long?>? getProcessStartMs = null,
        Func<IReadOnlyList<OpenClawProcessInfo>>? listProcesses = null,
        Func<DateTime>? utcNow = null,
        Func<long>? getTicksMs = null,
        string? stateDirOverride = null,
        string? localAppDataOverride = null,
        Action<string>? onEnumerateSessionsDir = null)
    {
        if (!IsOpenClawEnabled())
        {
            ResetOpenClawCache();
            yield break;
        }

        var home = homeDir ?? Home;
        var explicitStateDir = homeDir is null
            ? (stateDirOverride ?? Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR"))
            : stateDirOverride;
        var explicitLocalAppData = homeDir is null
            ? (localAppDataOverride ?? (OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : null))
            : localAppDataOverride;

        var stateDirs = BuildOpenClawStateDirs(home, explicitStateDir);
        var windowsLocksDir = !string.IsNullOrWhiteSpace(explicitLocalAppData) && Path.IsPathRooted(explicitLocalAppData)
            ? Path.Combine(Path.GetFullPath(explicitLocalAppData), "OpenClaw", "locks")
            : null;

        var scopeKey = string.Join("|", stateDirs) + "|" + (windowsLocksDir ?? "");
        if (!string.Equals(_openClawScopeKey, scopeKey, OpenClawPathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            ResetOpenClawCache();
            _openClawScopeKey = scopeKey;
        }

        var aliveCheck = isAlive ?? Alive;
        var startLookup = getProcessStartMs ?? GetProcessStartMs;
        var nowUtc = utcNow?.Invoke() ?? DateTime.UtcNow;
        var tickNow = getTicksMs?.Invoke() ?? Environment.TickCount64;

        var gateway = FindOpenClawGateway(stateDirs, windowsLocksDir, aliveCheck, startLookup, listProcesses, tickNow);
        if (gateway is not { } gw) yield break;

        var newestWriteUtc = ReadOpenClawSessionActivity(stateDirs, nowUtc, tickNow, onEnumerateSessionsDir);
        var startedAt = gw.StartMs ?? gw.LockTimeMs ?? 0;
        var hasActivity = newestWriteUtc > DateTime.MinValue;
        var status = hasActivity && (nowUtc - newestWriteUtc) < TimeSpan.FromMinutes(2) ? "busy" : "idle";
        var since = hasActivity
            ? Math.Max(new DateTimeOffset(newestWriteUtc).ToUnixTimeMilliseconds(), startedAt)
            : startedAt;

        yield return new AgentSession(
            Id: "openclaw:gateway",
            Tool: "openclaw",
            Name: "OpenClaw",
            Project: "Gateway",
            Status: status,
            WaitingFor: null,
            Since: since,
            StartedAt: startedAt,
            Pid: gw.Pid);
    }

    private static List<string> BuildOpenClawStateDirs(string home, string? openClawStateDir)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(OpenClawPathComparer);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir)) return;
            var full = Path.GetFullPath(dir);
            if (seen.Add(full)) dirs.Add(full);
        }

        Add(openClawStateDir);
        Add(Path.Combine(home, ".openclaw"));
        return dirs;
    }

    private OpenClawGatewayMatch? FindOpenClawGateway(
        IReadOnlyList<string> stateDirs,
        string? windowsLocksDir,
        Func<int, bool> aliveCheck,
        Func<int, long?> startLookup,
        Func<IReadOnlyList<OpenClawProcessInfo>>? listProcesses,
        long tickNow)
    {
        Dictionary<int, long>? lockTimeByPid = null;
        IReadOnlyList<OpenClawProcessInfo>? testProcesses = null;
        Func<IReadOnlyList<OpenClawProcessInfo>>? verifyListProcesses = listProcesses is null
            ? null
            : () => testProcesses ??= listProcesses();

        foreach (var (lockFile, requireGatewayRole) in EnumerateOpenClawLockFiles(stateDirs, windowsLocksDir))
        {
            if (TryParseOpenClawLock(lockFile, requireGatewayRole, out var pid, out var lockWriteMs, out var lockCreatedMs)
                && pid > 0
                && aliveCheck(pid))
            {
                var lockTimeMs = lockCreatedMs ?? lockWriteMs;
                var procStartMs = startLookup(pid);
                if (procStartMs is { } ps)
                {
                    if (SameStartMs(ps, lockWriteMs, lockCreatedMs, () => VerifyOpenClawProcess(pid, ps, verifyListProcesses)))
                        return new OpenClawGatewayMatch(pid, ps, lockTimeMs);
                }
                else
                {
                    lockTimeByPid ??= new Dictionary<int, long>();
                    lockTimeByPid.TryAdd(pid, lockTimeMs);
                }
            }
        }

        if (_openClawCachedPid is { } cachedPid
            && _openClawCachedStartMs is { } cachedStart
            && aliveCheck(cachedPid)
            && startLookup(cachedPid) is { } actualStart
            && Math.Abs(actualStart - cachedStart) < 1_000)
        {
            return new OpenClawGatewayMatch(cachedPid, actualStart, null);
        }

        _openClawCachedPid = null;
        _openClawCachedStartMs = null;

        if (tickNow < _openClawNextProcDiscovery)
            return null;

        _openClawNextProcDiscovery = tickNow + OpenClawDiscoveryIntervalMs;
        var processes = testProcesses ?? listProcesses?.Invoke() ?? ScanOpenClawCandidateProcesses();
        var match = SelectOpenClawProcess(processes, aliveCheck);
        if (match is not { } found) return null;

        var startMs = found.StartMs ?? startLookup(found.Pid);
        if (startMs is { } validStart)
        {
            _openClawCachedPid = found.Pid;
            _openClawCachedStartMs = validStart;
            _openClawVerifiedIdentity = (found.Pid, validStart);
        }

        long? matchedLockTimeMs = lockTimeByPid is not null && lockTimeByPid.TryGetValue(found.Pid, out var lt) ? lt : null;
        return new OpenClawGatewayMatch(found.Pid, startMs, matchedLockTimeMs);
    }

    internal static IEnumerable<(FileInfo File, bool RequireGatewayRole)> EnumerateOpenClawLockFiles(
        IEnumerable<string>? stateDirs = null,
        string? windowsLocksDir = null)
    {
        var dirs = stateDirs ?? BuildOpenClawStateDirs(Home, Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR"));
        var resolvedWindowsLocks = stateDirs is null
            ? (windowsLocksDir ?? (OperatingSystem.IsWindows()
                && Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localApp
                ? Path.Combine(localApp, "OpenClaw", "locks")
                : null))
            : windowsLocksDir;

        var candidates = new List<(FileInfo File, bool RequireGatewayRole)>();
        var seen = new HashSet<string>(OpenClawPathComparer);

        void AddCandidate(FileInfo fi, bool requireGatewayRole)
        {
            try
            {
                if (fi.Exists && seen.Add(fi.FullName))
                    candidates.Add((fi, requireGatewayRole));
            }
            catch { }
        }

        void ScanLockDir(string dirPath)
        {
            if (!Directory.Exists(dirPath)) return;
            try
            {
                foreach (var f in new DirectoryInfo(dirPath).EnumerateFiles("*.lock"))
                {
                    var isGatewayNamed = f.Name.StartsWith("gateway", StringComparison.OrdinalIgnoreCase);
                    AddCandidate(f, requireGatewayRole: !isGatewayNamed);
                }
            }
            catch { }
        }

        foreach (var stateDir in dirs)
        {
            if (string.IsNullOrWhiteSpace(stateDir) || !Directory.Exists(stateDir)) continue;
            try
            {
                AddCandidate(new FileInfo(Path.Combine(stateDir, "gateway.lock")), requireGatewayRole: false);
                AddCandidate(new FileInfo(Path.Combine(stateDir, "gateway.pid")), requireGatewayRole: false);

                var stateLocksDir = Path.Combine(stateDir, "locks");
                AddCandidate(new FileInfo(Path.Combine(stateLocksDir, "gateway.lock")), requireGatewayRole: false);
                AddCandidate(new FileInfo(Path.Combine(stateLocksDir, "gateway.pid")), requireGatewayRole: false);

                var tmpDir = Path.Combine(stateDir, "tmp");
                if (Directory.Exists(tmpDir))
                {
                    foreach (var dir in new DirectoryInfo(tmpDir).EnumerateDirectories("openclaw*"))
                        ScanLockDir(dir.FullName);
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(resolvedWindowsLocks))
            ScanLockDir(resolvedWindowsLocks);

        return candidates.OrderByDescending(c => c.File.LastWriteTimeUtc);
    }

    private static bool TryParseOpenClawLock(
        FileInfo file,
        bool requireGatewayRole,
        out int pid,
        out long lockWriteMs,
        out long? lockCreatedMs)
    {
        pid = 0;
        lockWriteMs = 0;
        lockCreatedMs = null;
        try
        {
            if (!file.Exists || file.Length is <= 0 or > OpenClawMaxLockBytes) return false;
            lockWriteMs = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            var text = File.ReadAllText(file.FullName).Trim();
            if (text.Length == 0) return false;

            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                var role = Str(root, "role");
                if (requireGatewayRole)
                {
                    if (role is null || !role.Equals("gateway", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (role is not null && !role.Equals("gateway", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (!root.TryGetProperty("pid", out var pidProp) || !pidProp.TryGetInt32(out pid) || pid <= 0)
                    return false;
                if (Str(root, "createdAt") is { } created && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                    lockCreatedMs = dto.ToUnixTimeMilliseconds();
                return true;
            }

            if (requireGatewayRole) return false;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0;
        }
        catch
        {
            pid = 0;
            return false;
        }
    }

    /// <summary>
    /// Guard against PID reuse: a process started significantly after the lock/PID file was created or written
    /// cannot own it. If the lock was renewed long after startup without a createdAt field, verify process identity.
    /// </summary>
    internal static bool SameStartMs(long procStartMs, long lockWriteMs, long? lockCreatedMs = null, Func<bool>? verifyIdentity = null)
    {
        const long afterSkewMs = 5_000;
        const long startupWindowMs = 120_000;

        var refMs = lockCreatedMs ?? lockWriteMs;
        if (procStartMs > refMs + afterSkewMs || procStartMs > lockWriteMs + afterSkewMs)
            return false;
        if (refMs - procStartMs <= startupWindowMs)
            return true;
        return verifyIdentity?.Invoke() == true;
    }

    private bool VerifyOpenClawProcess(int pid, long procStartMs, Func<IReadOnlyList<OpenClawProcessInfo>>? listProcesses)
    {
        if (_openClawVerifiedIdentity is { } verified
            && verified.Pid == pid
            && Math.Abs(verified.StartMs - procStartMs) < 1_000)
        {
            return true;
        }

        if (IsOpenClawProcessPid(pid, listProcesses))
        {
            _openClawVerifiedIdentity = (pid, procStartMs);
            return true;
        }

        if (_openClawVerifiedIdentity is { } prev && prev.Pid == pid)
            _openClawVerifiedIdentity = null;
        return false;
    }

    private static bool IsOpenClawProcessPid(int pid, Func<IReadOnlyList<OpenClawProcessInfo>>? listProcesses)
    {
        if (listProcesses is not null)
        {
            var match = listProcesses().FirstOrDefault(p => p.Pid == pid);
            return match.Pid == pid && IsOpenClawProcessMatch(match);
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var name = p.ProcessName;
            if (IsDirectGatewayProcessName(name)) return true;
            if (!name.Equals("openclaw", StringComparison.OrdinalIgnoreCase) && !IsNodeLikeRuntime(name))
                return false;
            var cmd = ProcessCommandLine.Read(pid);
            return IsOpenClawProcessMatch(new OpenClawProcessInfo(pid, name, cmd, null));
        }
        catch
        {
            return false;
        }
    }

    private static OpenClawProcessInfo? SelectOpenClawProcess(IReadOnlyList<OpenClawProcessInfo> processes, Func<int, bool> aliveCheck)
    {
        OpenClawProcessInfo? cliCandidate = null;
        OpenClawProcessInfo? runtimeCandidate = null;
        foreach (var p in processes)
        {
            if (p.Pid <= 0 || !aliveCheck(p.Pid)) continue;
            if (IsDirectGatewayProcessName(p.Name))
                return p;
            if (cliCandidate is null && p.Name.Equals("openclaw", StringComparison.OrdinalIgnoreCase) && IsOpenClawProcessMatch(p))
                cliCandidate = p;
            else if (runtimeCandidate is null && IsNodeLikeRuntime(p.Name) && IsOpenClawGatewayCommandLine(p.CommandLine))
                runtimeCandidate = p;
        }
        return cliCandidate ?? runtimeCandidate;
    }

    private static bool IsDirectGatewayProcessName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Equals("openclaw-gateway", StringComparison.OrdinalIgnoreCase)
            || (OperatingSystem.IsLinux() && name.Equals("openclaw-gatewa", StringComparison.OrdinalIgnoreCase)));

    private static bool IsNodeLikeRuntime(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Equals("node", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bun", StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenClawProcessMatch(OpenClawProcessInfo p)
    {
        if (IsDirectGatewayProcessName(p.Name)) return true;
        if (p.Name.Equals("openclaw", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(p.CommandLine)) return true;
            var args = SplitArgs(p.CommandLine);
            if (args.Count == 1 && args[0].Replace('\\', '/').TrimEnd('/').EndsWith("openclaw", StringComparison.OrdinalIgnoreCase))
                return true;
            return IsOpenClawGatewayCommandLine(p.CommandLine);
        }
        return IsNodeLikeRuntime(p.Name) && IsOpenClawGatewayCommandLine(p.CommandLine);
    }

    internal static bool IsOpenClawGatewayCommandLine(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return false;
        var args = SplitArgs(cmd);
        if (args.Count == 0) return false;

        static string BaseName(string token)
        {
            var norm = token.Replace('\\', '/').TrimEnd('/');
            var slash = norm.LastIndexOf('/');
            var name = slash >= 0 ? norm[(slash + 1)..] : norm;
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".mjs", ".js" })
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^ext.Length];
                    break;
                }
            }
            return name;
        }

        if (BaseName(args[0]).Equals("openclaw-gateway", StringComparison.OrdinalIgnoreCase))
            return true;

        var hasOpenClawEntry = false;
        var gatewayIdx = -1;
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            var norm = token.Replace('\\', '/');
            var baseName = BaseName(token);
            if (i <= 1 && !token.StartsWith('-') && baseName.Equals("openclaw-gateway", StringComparison.OrdinalIgnoreCase))
                return true;

            if (gatewayIdx >= 0)
            {
                if (token is "--help" or "-h" or "--version")
                    return false;
                if (!token.StartsWith('-') && OpenClawCliSubcommands.Contains(token))
                {
                    if (!(token.Equals("token", StringComparison.OrdinalIgnoreCase)
                          && i > gatewayIdx + 1
                          && args[i - 1].Equals("--auth", StringComparison.OrdinalIgnoreCase)))
                        return false;
                }
                continue;
            }

            if (!token.StartsWith('-')
                && (baseName.Equals("openclaw", StringComparison.OrdinalIgnoreCase)
                    || norm.Contains("/openclaw/", StringComparison.OrdinalIgnoreCase)
                    || norm.EndsWith("/openclaw.mjs", StringComparison.OrdinalIgnoreCase)
                    || norm.Equals("openclaw.mjs", StringComparison.OrdinalIgnoreCase)))
            {
                hasOpenClawEntry = true;
            }
            else if (hasOpenClawEntry && token.Equals("gateway", StringComparison.OrdinalIgnoreCase))
            {
                gatewayIdx = i;
            }
        }
        return hasOpenClawEntry && gatewayIdx >= 0;
    }

    private static List<string> SplitArgs(string cmd)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var inToken = false;
        foreach (var ch in cmd)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else sb.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                inToken = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (inToken) { args.Add(sb.ToString()); sb.Clear(); inToken = false; }
            }
            else
            {
                sb.Append(ch);
                inToken = true;
            }
        }
        if (inToken) args.Add(sb.ToString());
        return args;
    }

    private static IReadOnlyList<OpenClawProcessInfo> ScanOpenClawCandidateProcesses()
    {
        var results = new List<OpenClawProcessInfo>();
        var needCmdline = new List<(int Pid, string Name, long? StartMs)>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return results; }

        try
        {
            foreach (var p in procs)
            {
                try
                {
                    var name = p.ProcessName;
                    if (!IsDirectGatewayProcessName(name)
                        && !name.Equals("openclaw", StringComparison.OrdinalIgnoreCase)
                        && !IsNodeLikeRuntime(name))
                        continue;

                    long? startMs = null;
                    try { startMs = new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); } catch { }

                    if (IsDirectGatewayProcessName(name))
                        results.Add(new OpenClawProcessInfo(p.Id, name, null, startMs));
                    else
                        needCmdline.Add((p.Id, name, startMs));
                }
                catch { }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        if (needCmdline.Count > 0)
        {
            var cmds = ProcessCommandLine.ReadMany(needCmdline.Select(x => x.Pid).ToArray());
            foreach (var c in needCmdline)
                results.Add(new OpenClawProcessInfo(c.Pid, c.Name, cmds.GetValueOrDefault(c.Pid), c.StartMs));
        }
        return results;
    }

    private DateTime ReadOpenClawSessionActivity(
        IReadOnlyList<string> stateDirs,
        DateTime nowUtc,
        long tickNow,
        Action<string>? onEnumerateSessionsDir = null)
    {
        var activeRoots = new List<string>();
        foreach (var stateDir in stateDirs)
        {
            var agentsRoot = Path.Combine(stateDir, "agents");
            if (Directory.Exists(agentsRoot))
                activeRoots.Add(agentsRoot);
        }

        if (activeRoots.Count == 0)
        {
            _openClawAgentsDirWrites.Clear();
            _openClawAgentsSessionsDirs.Clear();
            _openClawDirCache.Clear();
            return DateTime.MinValue;
        }

        var activeRootSet = new HashSet<string>(activeRoots, OpenClawPathComparer);
        foreach (var goneRoot in _openClawAgentsDirWrites.Keys.Where(k => !activeRootSet.Contains(k)).ToArray())
        {
            _openClawAgentsDirWrites.Remove(goneRoot);
            if (_openClawAgentsSessionsDirs.Remove(goneRoot, out var goneDirs))
            {
                foreach (var d in goneDirs) _openClawDirCache.Remove(d);
            }
        }

        var fallbackElapsed = tickNow >= _openClawNextDiscovery;
        var discoveredSessionsDirs = new List<string>();

        foreach (var agentsRoot in activeRoots)
        {
            var agentsWriteUtc = Directory.GetLastWriteTimeUtc(agentsRoot);
            string[]? cachedDirs = null;
            var needRootScan = fallbackElapsed
                || !_openClawAgentsDirWrites.TryGetValue(agentsRoot, out var prevAgentsWriteUtc)
                || prevAgentsWriteUtc != agentsWriteUtc
                || !_openClawAgentsSessionsDirs.TryGetValue(agentsRoot, out cachedDirs)
                || cachedDirs.Any(d => !Directory.Exists(d));

            if (needRootScan)
            {
                var dirs = new List<string>();
                try
                {
                    foreach (var agentDir in new DirectoryInfo(agentsRoot).EnumerateDirectories())
                    {
                        var sessionsPath = Path.Combine(agentDir.FullName, "sessions");
                        if (Directory.Exists(sessionsPath))
                            dirs.Add(sessionsPath);
                    }
                }
                catch { }

                if (_openClawAgentsSessionsDirs.TryGetValue(agentsRoot, out var oldDirs))
                {
                    var newSet = new HashSet<string>(dirs, OpenClawPathComparer);
                    foreach (var oldDir in oldDirs)
                    {
                        if (!newSet.Contains(oldDir))
                            _openClawDirCache.Remove(oldDir);
                    }
                }

                cachedDirs = dirs.ToArray();
                _openClawAgentsSessionsDirs[agentsRoot] = cachedDirs;
                _openClawAgentsDirWrites[agentsRoot] = agentsWriteUtc;
            }

            discoveredSessionsDirs.AddRange(cachedDirs!);
        }

        if (fallbackElapsed)
            _openClawNextDiscovery = tickNow + OpenClawDiscoveryIntervalMs;

        var cutoff = nowUtc.AddHours(-3);
        var newestWriteUtc = DateTime.MinValue;

        foreach (var sessionsPath in discoveredSessionsDirs)
        {
            DateTime dirWriteUtc;
            try
            {
                if (!Directory.Exists(sessionsPath))
                {
                    _openClawDirCache.Remove(sessionsPath);
                    continue;
                }
                dirWriteUtc = Directory.GetLastWriteTimeUtc(sessionsPath);
            }
            catch
            {
                continue;
            }

            if (!_openClawDirCache.TryGetValue(sessionsPath, out var cached)
                || cached.DirWriteUtc != dirWriteUtc)
            {
                onEnumerateSessionsDir?.Invoke(sessionsPath);
                var files = EnumerateSessionCandidateFiles(sessionsPath, cutoff);
                cached = (dirWriteUtc, files);
                _openClawDirCache[sessionsPath] = cached;
            }

            foreach (var path in cached.CandidateFiles)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.LastWriteTimeUtc > newestWriteUtc)
                        newestWriteUtc = fi.LastWriteTimeUtc;
                }
                catch { }
            }
        }

        return newestWriteUtc;
    }

    private static string[] EnumerateSessionCandidateFiles(string sessionsPath, DateTime cutoff)
    {
        var recent = new List<string>();
        FileInfo? newestOverall = null;
        try
        {
            foreach (var f in new DirectoryInfo(sessionsPath).EnumerateFiles("*.jsonl"))
            {
                if (f.Name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Contains(".deleted.", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (f.LastWriteTimeUtc > cutoff)
                    recent.Add(f.FullName);
                if (newestOverall is null || f.LastWriteTimeUtc > newestOverall.LastWriteTimeUtc)
                    newestOverall = f;
            }
        }
        catch { }

        if (newestOverall is not null && !recent.Contains(newestOverall.FullName, OpenClawPathComparer))
            recent.Add(newestOverall.FullName);

        return recent.ToArray();
    }

    private static long? GetProcessStartMs(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            return new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        }
        catch { return null; }
    }
}
