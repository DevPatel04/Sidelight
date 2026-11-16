using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Status">busy | waiting | shell | idle</param>
public sealed record AgentSession(string Id, string Tool, string Name, string Project, string Status,
    string? WaitingFor, long Since, long StartedAt, int? Pid = null,
    string? Model = null, string? Effort = null, long? TotalTokens = null, long? InputTokens = null,
    long? OutputTokens = null, long? CachedInputTokens = null, long? ReasoningTokens = null,
    long? LastInputTokens = null, long? ContextWindow = null, long? TurnStartedAt = null,
    string? Outcome = null, string? LastTool = null);

internal readonly record struct AntigravityProcessCandidate(
    int Pid,
    string Name,
    long FileTimeUtc,
    long UnixMs,
    string? CommandLine);

/// <summary>
/// Watches running AI coding sessions (local and read-only):
///  - Claude Code: ~/.claude/sessions/{pid}.json (status files written by Claude Code itself)
///  - Codex: the latest events in recent rollout files under ~/.codex/sessions
///  - Antigravity: ~/.gemini/antigravity daemon/conversation state
/// </summary>
public sealed class SessionMonitor(ILogger<SessionMonitor> log) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly HashSet<string> SpecificAntigravityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Antigravity", "Antigravity IDE", "agy",
    };
    private static readonly HashSet<string> GenericAntigravityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "language_server", "language-server",
        "language_server_macos", "language_server_macos_arm",
        "language_server_linux", "language_server_windows_x64",
        "language_server_linux_x64", "language_server_linux_arm",
    };
    internal static readonly string[] AntigravityProcessNames =
    [
        "Antigravity", "Antigravity IDE", "agy",
        "language_server", "language-server",
        "language_server_macos", "language_server_macos_arm",
        "language_server_linux", "language_server_windows_x64",
        "language_server_linux_x64", "language_server_linux_arm",
    ];
    private static readonly long DaemonStartToleranceTicks = TimeSpan.FromSeconds(30).Ticks;

    private readonly ConcurrentDictionary<string, string> _codexCwd = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, long Length, CodexDetails Details)> _codexDetails = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, int? Pid, long? ProcStart, string? Project)> _agyDaemonCache = new();
    private readonly ConcurrentDictionary<(int Pid, long StartKey), long> _agyProcStartCache = new();
    private readonly ConcurrentDictionary<(int Pid, long FileTimeUtc), bool> _agyCmdMatchCache = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length, bool IsBusy, string? Outcome)> _agyTranscriptCache = new();
    private volatile IReadOnlyList<AgentSession> _sessions = [];
    private string? _discoveryRoot;
    private long _nextDiscovery;
    private string[] _candidateFiles = [];
    private (int Pid, long FileTimeUtc, long UnixMs, string Name, bool IsGeneric)? _agyCachedProc;
    private long? _agyLastProcScanTicks;
    private string? _agyRoot;
    private long? _agyLastActivityDiscoveryTicks;
    private string[] _agyCandidatePbFiles = [];
    private string[] _agyCandidateTranscripts = [];
    internal int AgyDaemonParseCount { get; private set; }

    public IReadOnlyList<AgentSession> Snapshot() => _sessions;

    internal void SetSessionsForTest(IReadOnlyList<AgentSession> sessions) => _sessions = sessions;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var list = new List<AgentSession>();
            try { list.AddRange(ReadClaude()); } catch (Exception ex) { log.LogDebug("claude sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadCodex()); } catch (Exception ex) { log.LogDebug("codex sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadAntigravity()); } catch (Exception ex) { log.LogDebug("antigravity sessions: {Msg}", ex.Message); }
            _sessions = list;
            await Task.Delay(2000, ct);
        }
    }

    // CodexBar runs `claude` in <Application Support or temp>/CodexBar/ClaudeProbe to read usage.
    // Match only that exact directory so real projects that merely contain "CodexBar" stay visible.
    internal static bool IsCodexBarProbe(string cwd) =>
        cwd.Replace('\\', '/').TrimEnd('/').EndsWith("/CodexBar/ClaudeProbe", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<AgentSession> ReadClaude(string? homeDir = null, Func<int, bool>? isAlive = null)
    {
        var home = homeDir ?? Home;
        var aliveCheck = isAlive ?? Alive;
        var dir = Path.Combine(home, ".claude", "sessions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            AgentSession? s = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                if (pid == 0 || !aliveCheck(pid)) continue;
                // a session that was killed leaves its file behind; Windows may give its PID to another program later
                if (OperatingSystem.IsWindows() && long.TryParse(Str(r, "procStart"), out var procStart) && !SameStart(pid, procStart)) continue;
                var cwd = Str(r, "cwd") ?? "";
                var name = Str(r, "name") ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));

                if (IsCodexBarProbe(cwd)) continue;

                s = new AgentSession(
                    Id: "claude:" + (Str(r, "sessionId") ?? pid.ToString()),
                    Tool: "claude",
                    Name: name,
                    Project: project,
                    Status: Str(r, "status") ?? "idle",
                    WaitingFor: Str(r, "waitingFor"),
                    Since: Long(r, "statusUpdatedAt") ?? Long(r, "updatedAt") ?? 0,
                    StartedAt: Long(r, "startedAt") ?? 0,
                    Pid: pid);
            }
            catch { /* file was being written: retry on the next pass */ }
            if (s is not null) yield return s;
        }
    }

    internal IEnumerable<AgentSession> ReadCodex(string? sessionsRoot = null)
    {
        var root = sessionsRoot ?? Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) yield break;
        var cutoff = DateTime.UtcNow.AddHours(-3);
        // A resumed session appends to its original directory, even months later.
        if (_discoveryRoot != root || Environment.TickCount64 >= _nextDiscovery)
        {
            _candidateFiles = new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc > cutoff).Select(f => f.FullName).ToArray();
            _discoveryRoot = root;
            _nextDiscovery = Environment.TickCount64 + 30_000;
        }
        var files = _candidateFiles.Select(path => new FileInfo(path)).Where(f => f.Exists && f.LastWriteTimeUtc > cutoff).ToArray();
        var active = files.Select(f => f.FullName).ToHashSet();
        foreach (var old in _codexDetails.Keys.Where(p => !active.Contains(p)))
        { _codexDetails.TryRemove(old, out _); _codexCwd.TryRemove(old, out _); }

        foreach (var f in files)
        {
            AgentSession? s = null;
            try
            {
                var cwd = _codexCwd.GetOrAdd(f.FullName, ReadCodexCwd);
                var hasCached = _codexDetails.TryGetValue(f.FullName, out var cached);
                var details = hasCached && cached.LastWrite == f.LastWriteTimeUtc && cached.Length == f.Length
                    ? cached.Details : ReadCodexDetails(f.FullName,
                        hasCached && f.Length > cached.Length ? cached.Details : null,
                        hasCached && f.Length > cached.Length ? cached.Details.ReadOffset : 0);
                _codexDetails[f.FullName] = (f.LastWriteTimeUtc, f.Length, details);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var status = details.Lifecycle == "task_started" && age < TimeSpan.FromMinutes(3) ? "busy" : "idle";
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var id = f.Name.Length > 8 ? f.Name[^14..^6] : f.Name;
                s = new AgentSession("codex:" + f.Name, "codex", $"{project.ToLowerInvariant()}-{id[..4]}", project,
                    status, null, details.LastActivityAt ?? new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds(),
                    Model: details.Model, Effort: details.Effort, TotalTokens: details.TotalTokens,
                    InputTokens: details.InputTokens, OutputTokens: details.OutputTokens,
                    CachedInputTokens: details.CachedInputTokens, ReasoningTokens: details.ReasoningTokens,
                    LastInputTokens: details.LastInputTokens, ContextWindow: details.ContextWindow,
                    TurnStartedAt: details.TurnStartedAt, Outcome: details.Lifecycle switch
                    { "task_complete" => "completed", "turn_aborted" => "aborted", _ => null },
                    LastTool: details.LastTool);
            }
            catch { }
            if (s is not null) yield return s;
        }
    }

    private static string ReadCodexCwd(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var first = sr.ReadLine();
        if (first is null) return "";
        using var doc = JsonDocument.Parse(first);
        return doc.RootElement.TryGetProperty("payload", out var p) ? Str(p, "cwd") ?? "" : "";
    }

    private sealed record CodexDetails(string? Model, string? Effort, long? TotalTokens, long? InputTokens,
        long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens, long? LastInputTokens,
        long? ContextWindow, long? LastActivityAt, long? TurnStartedAt, string? Lifecycle, string? LastTool, long ReadOffset);

    /// <summary>Scans metadata and counters once, then follows appended lines; no prompts or tool arguments leave the Mac.</summary>
    private static CodexDetails ReadCodexDetails(string path, CodexDetails? previous, long previousLength)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = previous is null ? 0 : previousLength;
        fs.Seek(start, SeekOrigin.Begin);
        using var buffer = new MemoryStream();
        fs.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var lines = Encoding.UTF8.GetString(bytes, 0, complete).Split('\n');
        var model = previous?.Model;
        var effort = previous?.Effort;
        var lifecycle = previous?.Lifecycle;
        var lastTool = previous?.LastTool;
        var lastActivityAt = previous?.LastActivityAt;
        var turnStartedAt = previous?.TurnStartedAt;
        var totalTokens = previous?.TotalTokens;
        var inputTokens = previous?.InputTokens;
        var outputTokens = previous?.OutputTokens;
        var cachedInputTokens = previous?.CachedInputTokens;
        var reasoningTokens = previous?.ReasoningTokens;
        var lastInputTokens = previous?.LastInputTokens;
        var contextWindow = previous?.ContextWindow;
        foreach (var l in lines)
        {
            if (!l.Contains("\"event_msg\"") && !l.Contains("\"turn_context\"")
                && !l.Contains("\"custom_tool_call\"") && !l.Contains("\"function_call\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                var type = Str(root, "type");
                var eventType = Str(payload, "type");
                var ts = DateTimeOffset.TryParse(Str(root, "timestamp"), out var dt) ? dt.ToUnixTimeMilliseconds() : (long?)null;
                if (type == "event_msg")
                {
                    if (eventType != "token_count") lastActivityAt = ts;
                    if (eventType == "token_count"
                        && payload.TryGetProperty("info", out var tokenInfo) && tokenInfo.ValueKind == JsonValueKind.Object)
                    {
                        var total = tokenInfo.TryGetProperty("total_token_usage", out var t) ? t : default;
                        var last = tokenInfo.TryGetProperty("last_token_usage", out var u) ? u : default;
                        totalTokens = Long(total, "total_tokens") ?? totalTokens;
                        inputTokens = Long(total, "input_tokens") ?? inputTokens;
                        outputTokens = Long(total, "output_tokens") ?? outputTokens;
                        cachedInputTokens = Long(total, "cached_input_tokens") ?? cachedInputTokens;
                        reasoningTokens = Long(total, "reasoning_output_tokens") ?? reasoningTokens;
                        lastInputTokens = Long(last, "input_tokens") ?? lastInputTokens;
                        contextWindow = Long(tokenInfo, "model_context_window") ?? contextWindow;
                    }
                    if (eventType is "task_started" or "task_complete" or "turn_aborted")
                    {
                        lifecycle = eventType;
                        if (eventType == "task_started") turnStartedAt = ts;
                    }
                }
                else if (type == "turn_context")
                {
                    model = Str(payload, "model") ?? model;
                    effort = Str(payload, "effort") ?? effort;
                }
                else if (type == "response_item" && eventType is "function_call" or "custom_tool_call")
                    lastTool = Str(payload, "name") ?? lastTool;
            }
            catch (JsonException) { }
        }
        return new CodexDetails(model, effort, totalTokens, inputTokens, outputTokens, cachedInputTokens,
            reasoningTokens, lastInputTokens, contextWindow, lastActivityAt, turnStartedAt, lifecycle, lastTool, start + complete);
    }

    /// <summary>Claude Code writes its process' start time (a FILETIME) into the session file; another process with the same PID started later.</summary>
    private static bool SameStart(int pid, long fileTimeUtc, long toleranceTicks = 10_000_000)
    {
        try { using var p = Process.GetProcessById(pid); return Math.Abs(p.StartTime.ToFileTimeUtc() - fileTimeUtc) < toleranceTicks; } // 1 s default
        catch { return true; }
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static IReadOnlyList<AntigravityProcessCandidate> ScanOsAntigravityCandidates()
    {
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return []; }
        var list = new List<AntigravityProcessCandidate>();
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    var name = p.ProcessName;
                    if (!SpecificAntigravityNames.Contains(name) && !GenericAntigravityNames.Contains(name))
                        continue;
                    var pid = p.Id;
                    if (pid <= 0) continue;
                    long ft = 0, ms = 0;
                    try
                    {
                        var startUtc = p.StartTime.ToUniversalTime();
                        ft = startUtc.ToFileTimeUtc();
                        ms = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds();
                    }
                    catch { }
                    list.Add(new AntigravityProcessCandidate(pid, name, ft, ms, null));
                }
                catch { }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return list;
    }

    private static IReadOnlyList<AntigravityProcessCandidate> BuildCandidatesFromProcessIds(Func<string, int[]> getProcessIds)
    {
        var list = new List<AntigravityProcessCandidate>();
        foreach (var name in AntigravityProcessNames)
        {
            foreach (var pid in getProcessIds(name))
            {
                if (pid > 0)
                    list.Add(new AntigravityProcessCandidate(pid, name, 0, 0, null));
            }
        }
        return list;
    }

    private bool HasAntigravityCommandLineMarker(
        AntigravityProcessCandidate candidate,
        bool allowOsFallback,
        Func<int, string?>? getCommandLine)
    {
        if (candidate.CommandLine is not null)
            return candidate.CommandLine.Contains("antigravity", StringComparison.OrdinalIgnoreCase);

        if (!allowOsFallback && getCommandLine is null)
            return false;

        if (candidate.FileTimeUtc > 0 && _agyCmdMatchCache.TryGetValue((candidate.Pid, candidate.FileTimeUtc), out var cached))
            return cached;

        string? cmd = null;
        try { cmd = (getCommandLine ?? ProcessCommandLine.Read)(candidate.Pid); } catch { }
        var matched = !string.IsNullOrWhiteSpace(cmd)
            && cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase);

        if (candidate.FileTimeUtc > 0)
        {
            if (_agyCmdMatchCache.Count >= 256) _agyCmdMatchCache.Clear();
            _agyCmdMatchCache[(candidate.Pid, candidate.FileTimeUtc)] = matched;
        }
        return matched;
    }

    private static bool IsSameOsAntigravityProcess(int pid, string expectedName, bool isGeneric, Func<int, string?>? getCommandLine)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!isGeneric) return true;
            var cmd = (getCommandLine ?? ProcessCommandLine.Read)(pid);
            return !string.IsNullOrWhiteSpace(cmd)
                && cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsBetterAntigravityCandidate(
        AntigravityProcessCandidate candidate,
        int priority,
        (AntigravityProcessCandidate Candidate, int Priority)? best)
    {
        if (best is null) return true;
        var current = best.Value;
        if (priority != current.Priority) return priority < current.Priority;
        if (candidate.FileTimeUtc > 0 && current.Candidate.FileTimeUtc <= 0) return true;
        if (candidate.FileTimeUtc <= 0 && current.Candidate.FileTimeUtc > 0) return false;
        if (candidate.FileTimeUtc > 0 && candidate.FileTimeUtc != current.Candidate.FileTimeUtc)
            return candidate.FileTimeUtc < current.Candidate.FileTimeUtc;
        return candidate.Pid < current.Candidate.Pid;
    }

    private (int Pid, long FileTimeUtc, long UnixMs)? TryGetAntigravityProcess(
        Func<int, bool> aliveCheck,
        Func<int, long, bool>? sameStart,
        Func<IReadOnlyList<AntigravityProcessCandidate>>? scanProcesses,
        Func<int, string?>? getCommandLine,
        bool allowOsFallback,
        HashSet<int> validatedDaemonPids,
        long nowTicks)
    {
        if (_agyCachedProc is { } cached && cached.Pid > 0 && aliveCheck(cached.Pid))
        {
            var stillSame = cached.FileTimeUtc > 0
                ? (sameStart?.Invoke(cached.Pid, cached.FileTimeUtc) ?? SameStart(cached.Pid, cached.FileTimeUtc))
                : (allowOsFallback && IsSameOsAntigravityProcess(cached.Pid, cached.Name, cached.IsGeneric, getCommandLine));
            if (stillSame)
                return (cached.Pid, cached.FileTimeUtc, cached.UnixMs);
            _agyCachedProc = null;
        }
        else
        {
            _agyCachedProc = null;
        }

        if (_agyLastProcScanTicks is not null && nowTicks - _agyLastProcScanTicks.Value < 30_000)
            return null;
        _agyLastProcScanTicks = nowTicks;

        var candidates = (scanProcesses ?? ScanOsAntigravityCandidates)();
        (AntigravityProcessCandidate Candidate, int Priority)? best = null;
        var genericCandidates = new List<AntigravityProcessCandidate>();

        foreach (var c in candidates)
        {
            if (c.Pid <= 0 || !aliveCheck(c.Pid)) continue;
            if (SpecificAntigravityNames.Contains(c.Name))
            {
                var priority = c.Name.Equals("agy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (IsBetterAntigravityCandidate(c, priority, best))
                    best = (c, priority);
            }
            else if (GenericAntigravityNames.Contains(c.Name))
            {
                genericCandidates.Add(c);
            }
        }

        if (best is null)
        {
            foreach (var c in genericCandidates)
            {
                if (!HasAntigravityCommandLineMarker(c, allowOsFallback, getCommandLine)
                    && !validatedDaemonPids.Contains(c.Pid))
                    continue;
                if (IsBetterAntigravityCandidate(c, 2, best))
                    best = (c, 2);
            }
        }

        if (best is null) return null;

        var winner = best.Value.Candidate;
        if (winner.FileTimeUtc > 0 || allowOsFallback)
            _agyCachedProc = (winner.Pid, winner.FileTimeUtc, winner.UnixMs, winner.Name, best.Value.Priority == 2);
        return (winner.Pid, winner.FileTimeUtc, winner.UnixMs);
    }

    private static (long FileTimeUtc, long UnixMs)? TryGetProcessStart(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var startUtc = p.StartTime.ToUniversalTime();
            return (startUtc.ToFileTimeUtc(), new DateTimeOffset(startUtc).ToUnixTimeMilliseconds());
        }
        catch { return null; }
    }

    private static bool HasUnsafeProjectChars(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.Surrogate)
                return true;
        }
        return false;
    }

    internal static string? ProjectNameFromPathOrUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var candidate = raw.Trim();
        if (HasUnsafeProjectChars(candidate)) return null;
        if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : Uri.UnescapeDataString(candidate["file://".Length..]);
        }
        else
        {
            candidate = Uri.UnescapeDataString(candidate);
        }
        if (HasUnsafeProjectChars(candidate)) return null;
        candidate = candidate.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var name = Path.GetFileName(candidate);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || (name.Length == 2 && name[1] == ':') || name.Length > 100 || HasUnsafeProjectChars(name))
            return null;
        return name;
    }

    /// <summary>
    /// Normalizes daemon <c>procStart</c> values (Unix seconds, Unix milliseconds, or Windows FILETIME ticks)
    /// into Windows FILETIME UTC ticks for <see cref="SameStart"/>.
    /// </summary>
    internal static long? TryNormalizeProcStartToFileTimeUtc(long raw)
    {
        if (raw <= 0) return null;
        if (raw < 100_000_000_000L)
            return DateTimeOffset.FromUnixTimeSeconds(raw).UtcDateTime.ToFileTimeUtc();
        if (raw < 10_000_000_000_000_000L)
            return raw <= 253_402_300_799_999L
                ? DateTimeOffset.FromUnixTimeMilliseconds(raw).UtcDateTime.ToFileTimeUtc()
                : null;
        return raw;
    }

    private List<(int Pid, string? Project, DateTime FileWriteUtc)> CollectValidAntigravityDaemons(
        string agyDir,
        Func<int, bool> aliveCheck,
        Func<int, long, bool>? sameStart)
    {
        var valid = new List<(int Pid, string? Project, DateTime FileWriteUtc)>();
        var daemonDir = Path.Combine(agyDir, "daemon");
        if (!Directory.Exists(daemonDir)) return valid;
        try
        {
            var files = Directory.EnumerateFiles(daemonDir, "*.json").ToArray();
            var active = files.ToHashSet(StringComparer.Ordinal);
            foreach (var old in _agyDaemonCache.Keys.Where(p => !active.Contains(p)))
                _agyDaemonCache.TryRemove(old, out _);

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    var parsed = _agyDaemonCache.TryGetValue(f, out var cached)
                        && cached.LastWriteUtc == fi.LastWriteTimeUtc && cached.Length == fi.Length
                        ? cached
                        : ParseAntigravityDaemon(f, fi);
                    _agyDaemonCache[f] = parsed;

                    if (parsed.Pid is not { } pid || pid <= 0 || !aliveCheck(pid)) continue;
                    var fileTimeUtc = fi.LastWriteTimeUtc.ToFileTimeUtc();
                    if (parsed.ProcStart is { } explicitStart)
                    {
                        if (!(sameStart?.Invoke(pid, explicitStart) ?? SameStart(pid, explicitStart)))
                            continue;
                    }
                    else if (!(sameStart?.Invoke(pid, fileTimeUtc) ?? SameStart(pid, fileTimeUtc, DaemonStartToleranceTicks)))
                    {
                        continue;
                    }

                    valid.Add((pid, parsed.Project, fi.LastWriteTimeUtc));
                }
                catch { }
            }
        }
        catch { }
        return valid;
    }

    private (DateTime LastWriteUtc, long Length, int? Pid, long? ProcStart, string? Project) ParseAntigravityDaemon(string path, FileInfo fi)
    {
        AgyDaemonParseCount++;
        const int maxBytes = 64 * 1024;
        if (fi.Length is <= 0 or > maxBytes)
            return (fi.LastWriteTimeUtc, fi.Length, null, null, null);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[maxBytes + 1];
            var total = 0;
            while (total < buf.Length)
            {
                var n = fs.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            if (total == 0 || total > maxBytes)
                return (fi.LastWriteTimeUtc, fi.Length, null, null, null);

            using var doc = JsonDocument.Parse(buf.AsMemory(0, total));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (fi.LastWriteTimeUtc, fi.Length, null, null, null);

            var pid = root.TryGetProperty("pid", out var pElem) && pElem.ValueKind == JsonValueKind.Number && pElem.TryGetInt32(out var p) ? p : (int?)null;
            long? procStart = null;
            if (root.TryGetProperty("procStart", out var psElem))
            {
                if (psElem.ValueKind == JsonValueKind.Number && psElem.TryGetInt64(out var rawNum))
                    procStart = TryNormalizeProcStartToFileTimeUtc(rawNum);
                else if (psElem.ValueKind == JsonValueKind.String && long.TryParse(psElem.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawStr))
                    procStart = TryNormalizeProcStartToFileTimeUtc(rawStr);
            }
            var rawWorkspace = Str(root, "workspace") ?? Str(root, "workspaceFolder") ?? Str(root, "folder") ?? Str(root, "cwd");
            return (fi.LastWriteTimeUtc, fi.Length, pid, procStart, ProjectNameFromPathOrUri(rawWorkspace));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (fi.LastWriteTimeUtc, fi.Length, null, null, null);
        }
    }

    private long ResolveAntigravityStartMs(
        int pid,
        long startKey,
        Func<int, long?>? getProcessStartMs,
        (int Pid, long FileTimeUtc, long UnixMs)? procMatch,
        DateTime? daemonFileUtc)
    {
        var key = (pid, startKey);
        if (_agyProcStartCache.TryGetValue(key, out var cached))
            return cached;

        long? resolved = null;
        if (getProcessStartMs is not null)
            resolved = getProcessStartMs(pid);
        else if (procMatch is { UnixMs: > 0 } pm && pm.Pid == pid)
            resolved = pm.UnixMs;
        else if (TryGetProcessStart(pid) is { UnixMs: > 0 } ps)
            resolved = ps.UnixMs;

        if (resolved is { } ms and > 0)
        {
            if (_agyProcStartCache.Count >= 64) _agyProcStartCache.Clear();
            _agyProcStartCache[key] = ms;
            return ms;
        }
        return daemonFileUtc is { } dt ? new DateTimeOffset(dt).ToUnixTimeMilliseconds() : 0;
    }

    private (DateTime NewestActivityUtc, DateTime NewestTranscriptUtc, bool TranscriptIsBusy, string? TranscriptOutcome) ScanAntigravityActivity(string agyDir, long nowTicks)
    {
        var cutoff = DateTime.UtcNow.AddHours(-3);
        if (_agyLastActivityDiscoveryTicks is null || nowTicks - _agyLastActivityDiscoveryTicks.Value >= 30_000)
        {
            var pbCandidates = new List<string>();
            string? newestPbPath = null;
            var newestPbOverallTime = DateTime.MinValue;

            var convDir = Path.Combine(agyDir, "conversations");
            if (Directory.Exists(convDir))
            {
                try
                {
                    foreach (var f in new DirectoryInfo(convDir).EnumerateFiles())
                    {
                        if (!f.Extension.Equals(".pb", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (f.LastWriteTimeUtc > newestPbOverallTime) { newestPbOverallTime = f.LastWriteTimeUtc; newestPbPath = f.FullName; }
                        if (f.LastWriteTimeUtc > cutoff) pbCandidates.Add(f.FullName);
                    }
                }
                catch { }
            }
            if (newestPbPath is not null && !pbCandidates.Contains(newestPbPath, StringComparer.Ordinal))
                pbCandidates.Add(newestPbPath);

            var transcriptCandidates = new List<string>();
            string? newestTranscriptPath = null;
            var newestTranscriptOverallTime = DateTime.MinValue;

            var brainDir = Path.Combine(agyDir, "brain");
            if (Directory.Exists(brainDir))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(brainDir))
                    {
                        var transcript = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                        if (!File.Exists(transcript)) continue;
                        var fi = new FileInfo(transcript);
                        if (fi.LastWriteTimeUtc > newestTranscriptOverallTime) { newestTranscriptOverallTime = fi.LastWriteTimeUtc; newestTranscriptPath = fi.FullName; }
                        if (fi.LastWriteTimeUtc > cutoff) transcriptCandidates.Add(fi.FullName);
                    }
                }
                catch { }
            }
            if (newestTranscriptPath is not null && !transcriptCandidates.Contains(newestTranscriptPath, StringComparer.Ordinal))
                transcriptCandidates.Add(newestTranscriptPath);

            var activeTranscriptSet = transcriptCandidates.ToHashSet(StringComparer.Ordinal);
            foreach (var old in _agyTranscriptCache.Keys.Where(p => !activeTranscriptSet.Contains(p)))
                _agyTranscriptCache.TryRemove(old, out _);

            _agyCandidatePbFiles = [.. pbCandidates];
            _agyCandidateTranscripts = [.. transcriptCandidates];
            _agyLastActivityDiscoveryTicks = nowTicks;
        }

        var newestActivityUtc = DateTime.MinValue;
        foreach (var path in _agyCandidatePbFiles)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.LastWriteTimeUtc > newestActivityUtc)
                    newestActivityUtc = fi.LastWriteTimeUtc;
            }
            catch { }
        }

        FileInfo? newestTranscript = null;
        foreach (var path in _agyCandidateTranscripts)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                if (fi.LastWriteTimeUtc > newestActivityUtc)
                    newestActivityUtc = fi.LastWriteTimeUtc;
                if (newestTranscript is null || fi.LastWriteTimeUtc > newestTranscript.LastWriteTimeUtc)
                    newestTranscript = fi;
            }
            catch { }
        }

        if (newestTranscript is null)
            return (newestActivityUtc, DateTime.MinValue, false, null);

        var state = _agyTranscriptCache.TryGetValue(newestTranscript.FullName, out var cached)
            && cached.LastWriteUtc == newestTranscript.LastWriteTimeUtc && cached.Length == newestTranscript.Length
            ? cached
            : ParseAntigravityTranscriptState(newestTranscript);
        if (_agyTranscriptCache.Count >= 256 && !_agyTranscriptCache.ContainsKey(newestTranscript.FullName))
            _agyTranscriptCache.Clear();
        _agyTranscriptCache[newestTranscript.FullName] = state;

        return (newestActivityUtc, newestTranscript.LastWriteTimeUtc, state.IsBusy, state.Outcome);
    }

    private static (DateTime LastWriteUtc, long Length, bool IsBusy, string? Outcome) ParseAntigravityTranscriptState(FileInfo fi)
    {
        const int maxTailBytes = 64 * 1024;
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var truncatedHead = fs.Length > maxTailBytes;
            var len = (int)Math.Min(fs.Length, maxTailBytes);
            if (truncatedHead) fs.Seek(-len, SeekOrigin.End);
            var bytes = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(bytes, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return (fi.LastWriteTimeUtc, fi.Length, false, null);

            var startIdx = 0;
            if (truncatedHead)
            {
                var firstNl = Array.IndexOf(bytes, (byte)'\n', 0, read);
                if (firstNl < 0) return (fi.LastWriteTimeUtc, fi.Length, false, null);
                startIdx = firstNl + 1;
            }

            var lines = Encoding.UTF8.GetString(bytes, startIdx, read - startIdx).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        return (fi.LastWriteTimeUtc, fi.Length, true, null);
                    var type = Str(root, "type");
                    var status = Str(root, "status");
                    var hasToolCalls = root.TryGetProperty("tool_calls", out var tc)
                        && tc.ValueKind == JsonValueKind.Array
                        && tc.GetArrayLength() > 0;
                    if (type == "PLANNER_RESPONSE" && status is "DONE" or "ERROR" && !hasToolCalls)
                        return (fi.LastWriteTimeUtc, fi.Length, false, status == "ERROR" ? "aborted" : "completed");
                    return (fi.LastWriteTimeUtc, fi.Length, true, null);
                }
                catch (JsonException)
                {
                    // Partial or corrupted tail line: inspect the preceding complete line.
                }
            }
            return (fi.LastWriteTimeUtc, fi.Length, false, null);
        }
        catch
        {
            return (fi.LastWriteTimeUtc, fi.Length, false, null);
        }
    }

    internal IEnumerable<AgentSession> ReadAntigravity(
        string? homeDir = null,
        Func<string, int[]>? getProcessIds = null,
        Func<int, bool>? isAlive = null,
        Func<int, long, bool>? sameStart = null,
        Func<int, long?>? getProcessStartMs = null,
        Func<long>? getTicks = null,
        Func<IReadOnlyList<AntigravityProcessCandidate>>? scanProcesses = null,
        Func<int, string?>? getCommandLine = null)
    {
        var home = homeDir ?? Home;
        var agyDir = Path.Combine(home, ".gemini", "antigravity");
        if (!string.Equals(_agyRoot, agyDir, StringComparison.Ordinal))
        {
            _agyRoot = agyDir;
            _agyCachedProc = null;
            _agyLastProcScanTicks = null;
            _agyLastActivityDiscoveryTicks = null;
            _agyCandidatePbFiles = [];
            _agyCandidateTranscripts = [];
            _agyDaemonCache.Clear();
            _agyProcStartCache.Clear();
            _agyCmdMatchCache.Clear();
            _agyTranscriptCache.Clear();
        }
        if (!Directory.Exists(agyDir)) yield break;

        var aliveCheck = isAlive ?? Alive;
        var nowTicks = (getTicks ?? (() => Environment.TickCount64))();
        var validDaemons = CollectValidAntigravityDaemons(agyDir, aliveCheck, sameStart);
        var validatedDaemonPids = validDaemons.Select(d => d.Pid).ToHashSet();

        var allowOsFallback = scanProcesses is null && getProcessIds is null;
        Func<IReadOnlyList<AntigravityProcessCandidate>>? effectiveScan = scanProcesses ?? (getProcessIds is not null
            ? () => BuildCandidatesFromProcessIds(getProcessIds)
            : null);

        var procMatch = TryGetAntigravityProcess(
            aliveCheck, sameStart, effectiveScan, getCommandLine, allowOsFallback, validatedDaemonPids, nowTicks);
        int? agyPid = procMatch?.Pid;

        int? daemonPid = null;
        string? daemonProject = null;
        DateTime? daemonFileUtc = null;
        foreach (var d in validDaemons)
        {
            if (daemonPid is null || (agyPid is not null && d.Pid == agyPid.Value))
            {
                daemonPid = d.Pid;
                daemonFileUtc = d.FileWriteUtc;
                daemonProject = d.Project;
                if (agyPid is not null && d.Pid == agyPid.Value && daemonProject is not null) break;
            }
        }

        agyPid ??= daemonPid;
        if (agyPid is null) yield break;

        var startKey = procMatch is { FileTimeUtc: > 0 } m && m.Pid == agyPid.Value
            ? m.FileTimeUtc
            : daemonFileUtc?.ToFileTimeUtc()
                ?? (getProcessStartMs is null ? TryGetProcessStart(agyPid.Value)?.FileTimeUtc : null)
                ?? 0;
        var startedAt = ResolveAntigravityStartMs(agyPid.Value, startKey, getProcessStartMs, procMatch, daemonFileUtc);

        var (newestWriteUtc, newestTranscriptUtc, transcriptIsBusy, transcriptOutcome) = ScanAntigravityActivity(agyDir, nowTicks);
        var isRecentTranscript = newestTranscriptUtc > DateTime.MinValue
            && (DateTime.UtcNow - newestTranscriptUtc) < TimeSpan.FromMinutes(2);
        var status = isRecentTranscript && transcriptIsBusy ? "busy" : "idle";
        var since = newestWriteUtc > DateTime.MinValue
            ? new DateTimeOffset(newestWriteUtc).ToUnixTimeMilliseconds()
            : startedAt;
        if (startedAt > 0 && since < startedAt)
            since = startedAt;

        yield return new AgentSession(
            Id: "antigravity:main",
            Tool: "antigravity",
            Name: "Antigravity",
            Project: daemonProject ?? "Antigravity",
            Status: status,
            WaitingFor: null,
            Since: since,
            StartedAt: startedAt,
            Pid: agyPid,
            Outcome: status == "busy" ? null : transcriptOutcome);
    }

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}
