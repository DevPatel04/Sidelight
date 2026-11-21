using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace Sidelight.Agent;

/// <summary>
/// Tells whether the app that sent a hook request (the terminal running Claude Code, Cursor, Antigravity) owns the
/// focused window. The hook's curl is a descendant of that app, so the request's TCP connection leads to curl's process
/// and its parents lead to the app; when one of them owns the foreground window you are looking at the prompt already.
/// </summary>
/// <remarks>
/// Windows reads the TCP table and the process tree directly. Linux needs X11 and xdotool for the focused window;
/// Wayland has no desktop-wide focus query, and macOS would need Accessibility, so both answer "can't tell" and the
/// phone keeps asking as before. Every failure is "can't tell" for the same reason: a wrong "focused" would hide a
/// prompt the user wanted on the phone, while a wrong "not focused" only shows one they could also see at the PC.
/// </remarks>
public static class WindowFocus
{
    /// <summary>True when the process behind this loopback connection descends from the focused window's process,
    /// false when it does not, null when that can't be told on this computer.</summary>
    public static bool? CallerInFront(HttpContext ctx)
    {
        var conn = ctx.Connection;
        if (conn.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip) || conn.RemotePort <= 0) return null;
        try
        {
            if (ForegroundPid() is not { } front) return null;
            if (ConnectionOwner(conn.RemotePort, conn.LocalPort) is not { } caller) return null;
            return DescendsFrom(caller, front, ParentLookup());
        }
        catch { return null; }
    }

    /// <summary>
    /// True when <paramref name="ancestor"/> is <paramref name="pid"/> itself or one of its parents. Null when the
    /// chain breaks before it reaches a root without passing the ancestor (a parent that can't be read is not an answer).
    /// </summary>
    internal static bool? DescendsFrom(int pid, int ancestor, Func<int, int?> parentOf, int maxDepth = 64)
    {
        if (pid <= 0 || ancestor <= 0) return null;
        var seen = new HashSet<int>();
        for (var i = 0; i < maxDepth && pid > 0; i++)
        {
            if (pid == ancestor) return true;
            if (!seen.Add(pid)) return false; // a loop (pid reuse) can't reach the ancestor any more
            if (parentOf(pid) is not { } parent) return null;
            if (parent <= 0) return false; // reached the root
            pid = parent;
        }
        return pid == ancestor ? true : null;
    }

#if WINDOWS
    private static int? ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;
        return (int)pid;
    }

    /// <summary>The process that owns the client end of a loopback connection to the agent (curl's side).</summary>
    private static int? ConnectionOwner(int clientPort, int serverPort)
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var err = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (err == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue; // the table grew in between
                if (err != 0) return null;
                var count = Marshal.ReadInt32(buf);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 DWORDs);
                // ports sit in network byte order in the low 16 bits
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * 24;
                    var local = Port(Marshal.ReadInt32(row + 8));
                    var remote = Port(Marshal.ReadInt32(row + 16));
                    if (local == clientPort && remote == serverPort) return Marshal.ReadInt32(row + 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    /// <summary>
    /// Parent ids from one process snapshot. A parent that started after its child is a reused id, not the real parent,
    /// so the chain stops there instead of wandering into an unrelated app.
    /// </summary>
    private static Func<int, int?> ParentLookup()
    {
        var parents = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) return _ => null;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return pid =>
        {
            if (!parents.TryGetValue(pid, out var parent)) return null;
            if (parent > 0 && StartTime(parent) is { } ps && StartTime(pid) is { } cs && ps > cs) return 0;
            return parent;
        };
    }

    private static DateTime? StartTime(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch { return null; } // gone, or elevated: the snapshot's answer stands
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int af, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
#else
    private static int? ForegroundPid()
    {
        // X11 only: under Wayland xdotool would answer for XWayland windows alone, which is worse than no answer
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") != "x11"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        return int.TryParse(Sh.Run("xdotool", "getactivewindow", "getwindowpid")?.Trim(), out var pid) && pid > 0 ? pid : null;
    }

    /// <summary>The process holding the client end of a loopback connection: /proc/net/tcp gives the socket's inode,
    /// and the process whose descriptors include that socket is curl.</summary>
    private static int? ConnectionOwner(int clientPort, int serverPort)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var inode = SocketInode(Sh.ReadFile("/proc/net/tcp"), clientPort, serverPort)
            ?? SocketInode(Sh.ReadFile("/proc/net/tcp6"), clientPort, serverPort);
        if (inode is null) return null;
        var target = $"socket:[{inode}]";
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                    if (new FileInfo(fd).LinkTarget == target) return pid;
            }
            catch { } // another user's process, or gone
        }
        return null;
    }

    private static Func<int, int?> ParentLookup() => pid =>
    {
        // /proc/<pid>/stat: "pid (comm) state ppid ..."; comm may hold spaces and parentheses, so split after the last ')'
        var stat = Sh.ReadFile($"/proc/{pid}/stat");
        var close = stat?.LastIndexOf(')') ?? -1;
        if (close < 0) return null;
        var f = stat![(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return f.Length > 1 && int.TryParse(f[1], out var ppid) ? ppid : null;
    };
#endif

    /// <summary>The inode of the socket whose local port is the client's and whose remote port is the agent's, from a
    /// /proc/net/tcp listing (hex ports, inode in the tenth column).</summary>
    internal static string? SocketInode(string? table, int clientPort, int serverPort)
    {
        if (table is null) return null;
        foreach (var line in table.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10) continue;
            if (HexPort(f[1]) == clientPort && HexPort(f[2]) == serverPort && f[9] != "0") return f[9];
        }
        return null;
    }

    private static int? HexPort(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon >= 0 && int.TryParse(endpoint[(colon + 1)..], NumberStyles.HexNumber, null, out var p) ? p : null;
    }
}
