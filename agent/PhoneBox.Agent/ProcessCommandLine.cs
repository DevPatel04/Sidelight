using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>Reads process command lines across Windows, Linux, and macOS without invoking a shell.</summary>
internal static class ProcessCommandLine
{
    private const int MaxCommandLineBytes = 256 * 1024;
    private const int MacPsBatchSize = 64;

    public static string? Read(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsWindows()) return ReadWindows(pid);
        if (OperatingSystem.IsLinux()) return ReadLinux(pid);
        if (OperatingSystem.IsMacOS()) return ReadMacSingle(pid);
        return null;
    }

    /// <summary>
    /// Reads a process's arguments with their boundaries kept: exact NUL-separated argv on Linux, the Windows
    /// command-line parsing rules on Windows, and whitespace-separated <c>ps</c> output on macOS (ps does not quote).
    /// </summary>
    public static string[]? ReadArgv(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsLinux()) return ReadLinuxArgv(pid);
        if (OperatingSystem.IsWindows()) return ReadWindows(pid) is { } cmd ? SplitWindows(cmd) : null;
        if (OperatingSystem.IsMacOS()) return ReadMacSingle(pid) is { } mac ? SplitWhitespace(mac) : null;
        return null;
    }

    /// <summary>Batched <see cref="ReadArgv"/> (one <c>ps</c> call per 64 processes on macOS).</summary>
    public static Dictionary<int, string[]> ReadManyArgv(IReadOnlyList<int> pids)
    {
        var map = new Dictionary<int, string[]>();
        if (OperatingSystem.IsLinux())
        {
            foreach (var pid in pids.Where(p => p > 0).Distinct())
            {
                if (ReadLinuxArgv(pid) is { Length: > 0 } argv)
                    map[pid] = argv;
            }
            return map;
        }

        foreach (var (pid, cmd) in ReadMany(pids))
        {
            var argv = OperatingSystem.IsWindows() ? SplitWindows(cmd) : SplitWhitespace(cmd);
            if (argv.Length > 0) map[pid] = argv;
        }
        return map;
    }

    public static Dictionary<int, string> ReadMany(IReadOnlyList<int> pids)
    {
        var map = new Dictionary<int, string>();
        if (pids.Count == 0) return map;

        if (OperatingSystem.IsWindows())
        {
            foreach (var pid in pids.Where(p => p > 0).Distinct())
            {
                if (ReadWindows(pid) is { Length: > 0 } cmd)
                    map[pid] = cmd;
            }
            return map;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var pid in pids.Where(p => p > 0).Distinct())
            {
                if (ReadLinux(pid) is { Length: > 0 } cmd)
                    map[pid] = cmd;
            }
            return map;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var chunk in pids.Where(p => p > 0).Distinct().Chunk(MacPsBatchSize))
            {
                try
                {
                    var psi = new ProcessStartInfo("/bin/ps");
                    psi.ArgumentList.Add("-ww");
                    psi.ArgumentList.Add("-p");
                    psi.ArgumentList.Add(string.Join(",", chunk));
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add("pid=,args=");
                    CopyMinimalEnvironment(psi);
                    var res = ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
                    foreach (var line in res.Output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        var sep = trimmed.IndexOfAny([' ', '\t']);
                        if (sep > 0 && int.TryParse(trimmed[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                        {
                            var cmd = trimmed[(sep + 1)..].Trim();
                            if (cmd.Length > 0)
                                map[pid] = cmd.Length > MaxCommandLineBytes ? cmd[..MaxCommandLineBytes] : cmd;
                        }
                    }
                }
                catch { }
            }
        }

        return map;
    }

    private static string? ReadLinux(int pid) =>
        ReadLinuxArgv(pid) is { Length: > 0 } argv ? string.Join(' ', argv) : null;

    private static string[]? ReadLinuxArgv(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/cmdline";
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[MaxCommandLineBytes];
            var total = 0;
            while (total < buf.Length)
            {
                var n = fs.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            var argv = SplitNul(buf.AsSpan(0, total));
            return argv.Length > 0 ? argv : null;
        }
        catch { return null; }
    }

    /// <summary>Splits a Linux /proc/&lt;pid&gt;/cmdline buffer at its NUL separators, so spaces and quotes inside an argument stay inside it.</summary>
    internal static string[] SplitNul(ReadOnlySpan<byte> raw)
    {
        var args = new List<string>();
        while (raw.Length > 0)
        {
            var end = raw.IndexOf((byte)0);
            var part = end < 0 ? raw : raw[..end];
            args.Add(Encoding.UTF8.GetString(part));
            if (end < 0) break;
            raw = raw[(end + 1)..];
        }
        while (args.Count > 0 && args[^1].Length == 0) args.RemoveAt(args.Count - 1);
        return args.ToArray();
    }

    /// <summary>Splits a command line with the Windows (CommandLineToArgvW) rules: only double quotes group, an apostrophe is
    /// literal, <c>2n</c> backslashes before a quote become <c>n</c> and toggle quoting, <c>2n+1</c> become <c>n</c> plus a literal quote.</summary>
    internal static string[] SplitWindows(string cmd)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var inToken = false;
        for (var i = 0; i < cmd.Length; i++)
        {
            var ch = cmd[i];
            if (ch == '\\')
            {
                var start = i;
                while (i < cmd.Length && cmd[i] == '\\') i++;
                var count = i - start;
                if (i < cmd.Length && cmd[i] == '"')
                {
                    sb.Append('\\', count / 2);
                    if (count % 2 == 1) sb.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    sb.Append('\\', count);
                    i--;
                }
                inToken = true;
            }
            else if (ch == '"')
            {
                if (inQuotes && i + 1 < cmd.Length && cmd[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                inToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(ch))
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
        return args.ToArray();
    }

    private static string[] SplitWhitespace(string cmd) =>
        cmd.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

    private static string? ReadMacSingle(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/ps");
            psi.ArgumentList.Add("-ww");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("args=");
            CopyMinimalEnvironment(psi);
            var res = ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
            if (res.ExitCode != 0) return null;
            var cmd = res.Output.Trim();
            if (cmd.Length == 0) return null;
            return cmd.Length > MaxCommandLineBytes ? cmd[..MaxCommandLineBytes] : cmd;
        }
        catch { return null; }
    }

    private static void CopyMinimalEnvironment(ProcessStartInfo psi)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "PATH", "HOME", "USERPROFILE", "TEMP", "TMPDIR" })
        {
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } val)
                kept[key] = val;
        }
        psi.Environment.Clear();
        foreach (var kv in kept)
            psi.Environment[kv.Key] = kv.Value;
    }

    private static string? ReadWindows(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, 0, 0, out var len);
            if (len <= 0 || len > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
                var chars = (ushort)Marshal.ReadInt16(buf) / 2; // UNICODE_STRING.Length is in bytes
                if (chars <= 0) return null;
                var ptr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return ptr != 0 ? Marshal.PtrToStringUni(ptr, chars) : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(nint h, int infoClass, nint info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(nint h);
}
