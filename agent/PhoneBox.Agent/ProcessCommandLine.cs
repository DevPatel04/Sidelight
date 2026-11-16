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

    private static string? ReadLinux(int pid)
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
            if (total == 0) return null;
            for (var i = 0; i < total; i++)
            {
                if (buf[i] == 0) buf[i] = (byte)' ';
            }
            var cmd = Encoding.UTF8.GetString(buf, 0, total).Trim();
            return cmd.Length > 0 ? cmd : null;
        }
        catch { return null; }
    }

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
