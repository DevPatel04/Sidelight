using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class CloudPhotoService
{
    private static bool TryGetRelativeSegments(string rootFull, string fullFile, out string[] segments)
    {
        segments = [];
        var normRoot = NormalizeMacPath(rootFull).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normFile = NormalizeMacPath(fullFile);
        if (PathsEqual(normFile, normRoot) || !IsPathInsideRoot(normFile, normRoot, EnsureTrailingSep(normRoot)))
            return false;

        var rawRootSep = EnsureTrailingSep(rootFull);
        var rel = fullFile.StartsWith(rawRootSep, PathComparison)
            ? fullFile[rawRootSep.Length..]
            : Path.GetRelativePath(normRoot, normFile);

        if (string.IsNullOrWhiteSpace(rel) || rel == "." || Path.IsPathRooted(rel))
            return false;

        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (part is "." or "..") return false;
        }
        segments = parts;
        return true;
    }

    /// <summary>Walks every intermediate directory and the target file component, rejecting any symlink or reparse point.</summary>
    internal static bool HasNoIntermediateReparsePoints(string rootFull, string fullFile)
    {
        if (!TryGetRelativeSegments(rootFull, fullFile, out var parts))
            return false;

        var cur = rootFull;
        for (var i = 0; i < parts.Length; i++)
        {
            cur = Path.Combine(cur, parts[i]);
            try
            {
                if (i < parts.Length - 1)
                {
                    var di = new DirectoryInfo(cur);
                    if (!di.Exists || di.LinkTarget is not null || (di.Attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                }
                else
                {
                    var fi = new FileInfo(cur);
                    if (!fi.Exists || fi.LinkTarget is not null)
                        return false;
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0
                        && !(OperatingSystem.IsWindows() && IsWindowsCloudPlaceholder(fi.Attributes)))
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsOpenHandleInsideRoot(SafeFileHandle fileHandle, string rootFull)
    {
        if (fileHandle.IsInvalid) return false;

        if (OperatingSystem.IsMacOS())
        {
            var rootFd = DarwinOpen(rootFull, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
            if (rootFd < 0) return false;
            try
            {
                if (!TryGetMacFdPath(rootFd, out var rootReal)) return false;
                var added = false;
                fileHandle.DangerousAddRef(ref added);
                try
                {
                    if (!TryGetMacFdPath((int)fileHandle.DangerousGetHandle(), out var fileReal)) return false;
                    return IsPathInsideRoot(fileReal, rootReal, EnsureTrailingSep(rootReal));
                }
                finally
                {
                    if (added) fileHandle.DangerousRelease();
                }
            }
            finally
            {
                DarwinClose(rootFd);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using var rootHandle = WinCreateFile(rootFull, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (!TryGetWinHandleFinalPath(rootHandle, out var rootFinal)) return false;
            if (!TryGetWinHandleFinalPath(fileHandle, out var fileFinal)) return false;
            return IsPathInsideRoot(fileFinal, rootFinal, EnsureTrailingSep(rootFinal));
        }

        try
        {
            var added = false;
            fileHandle.DangerousAddRef(ref added);
            try
            {
                var fdLink = $"/proc/self/fd/{fileHandle.DangerousGetHandle()}";
                var fileTarget = File.ResolveLinkTarget(fdLink, returnFinalTarget: true)?.FullName;
                if (string.IsNullOrEmpty(fileTarget)) return false;
                var rootCanonical = CanonicalizeUnixDirectory(rootFull);
                return IsPathInsideRoot(fileTarget, rootCanonical, EnsureTrailingSep(rootCanonical));
            }
            finally
            {
                if (added) fileHandle.DangerousRelease();
            }
        }
        catch
        {
            return false;
        }
    }

    private static string CanonicalizeUnixDirectory(string fullPath)
    {
        var cur = Path.GetPathRoot(fullPath) ?? "/";
        var rest = fullPath[cur.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in rest)
        {
            cur = Path.Combine(cur, part);
            var target = Directory.ResolveLinkTarget(cur, returnFinalTarget: true);
            if (target is not null)
                cur = Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(cur);
    }

    private static byte[]? ReadBoundedFileStream(FileStream fs)
    {
        if (fs.CanSeek && fs.Length is <= 0 or > MaxImageBytes)
            return null;

        using var ms = new MemoryStream(fs.CanSeek && fs.Length is > 0 and <= 1024 * 1024 ? (int)fs.Length : 16384);
        var buffer = new byte[16384];
        long total = 0;
        while (true)
        {
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > MaxImageBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return total == 0 ? null : ms.ToArray();
    }

    private static async Task<byte[]?> ReadBoundedFileStreamAsync(FileStream fs, CancellationToken ct)
    {
        if (fs.CanSeek && fs.Length is <= 0 or > MaxImageBytes)
            return null;

        using var ms = new MemoryStream(fs.CanSeek && fs.Length is > 0 and <= 1024 * 1024 ? (int)fs.Length : 16384);
        var buffer = new byte[16384];
        long total = 0;
        while (true)
        {
            var read = await fs.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > MaxImageBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return total == 0 ? null : ms.ToArray();
    }

    private bool IsCloudOnlyPlaceholder(FileInfo fi)
    {
        if (_isCloudOnlyOverride is not null)
            return _isCloudOnlyOverride(fi);
        if (OperatingSystem.IsMacOS())
            return IsMacDatalessFile(fi.FullName);
        if (OperatingSystem.IsWindows())
            return IsWindowsCloudPlaceholder(fi.Attributes);
        return false;
    }

    private static bool IsWindowsCloudPlaceholder(FileAttributes attr) =>
        ((int)attr & (0x00400000 | 0x00040000 | (int)FileAttributes.Offline)) != 0;

    private static bool IsMacDatalessFile(string fullPath)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var al = new DarwinAttrList { bitmapcount = ATTR_BIT_MAP_COUNT, commonattr = ATTR_CMN_FLAGS };
            var rc = DarwinGetAttrList(fullPath, ref al, out var buf, (nuint)Marshal.SizeOf<DarwinFlagsAttrBuf>(), FSOPT_NOFOLLOW);
            return rc == 0 && (buf.flags & SF_DATALESS) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadMacFileWithPolicy(string rootFull, string fullFile, int policy)
    {
        var prev = DarwinGetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD);
        DarwinSetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD, policy);
        try
        {
            return OpenAndReadMacFileNoFollow(rootFull, fullFile);
        }
        finally
        {
            DarwinSetIoPolicy(IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES, IOPOL_SCOPE_THREAD,
                prev >= 0 ? prev : IOPOL_MATERIALIZE_DATALESS_FILES_DEFAULT);
        }
    }

    private static byte[]? OpenAndReadMacFileNoFollow(string rootFull, string fullFile)
    {
        if (!TryGetRelativeSegments(rootFull, fullFile, out var segments))
            return null;

        var curFd = DarwinOpen(rootFull, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
        if (curFd < 0) return null;

        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var nextFd = DarwinOpenAt(curFd, segments[i], O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
                DarwinClose(curFd);
                curFd = nextFd;
                if (curFd < 0) return null;
            }

            var fileFd = DarwinOpenAt(curFd, segments[^1], O_RDONLY | O_NOFOLLOW);
            DarwinClose(curFd);
            curFd = -1;
            if (fileFd < 0) return null;

            using var handle = new SafeFileHandle((IntPtr)fileFd, ownsHandle: true);
            if (!IsOpenHandleInsideRoot(handle, rootFull))
                return null;

            using var fs = new FileStream(handle, FileAccess.Read);
            return ReadBoundedFileStream(fs);
        }
        finally
        {
            if (curFd >= 0) DarwinClose(curFd);
        }
    }

    private static bool TryGetMacFdPath(int fd, out string path)
    {
        path = "";
        if (fd < 0) return false;
        var al = new DarwinAttrList { bitmapcount = ATTR_BIT_MAP_COUNT, commonattr = ATTR_CMN_FULLPATH };
        var buf = new byte[4096];
        if (DarwinFGetAttrList(fd, ref al, buf, (nuint)buf.Length, 0) != 0)
            return false;
        var offset = BitConverter.ToInt32(buf, 4);
        var len = BitConverter.ToInt32(buf, 8);
        var start = 4 + offset;
        if (offset < 8 || len <= 0 || start < 0 || start + len > buf.Length)
            return false;
        path = Encoding.UTF8.GetString(buf, start, len).TrimEnd('\0');
        return path.Length > 0;
    }

    private static bool TryGetWinHandleFinalPath(SafeFileHandle handle, out string path)
    {
        path = "";
        if (handle.IsInvalid) return false;
        var buf = new char[1024];
        var len = WinGetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
        if (len == 0) return false;
        if (len > buf.Length)
        {
            buf = new char[len];
            len = WinGetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
            if (len == 0 || len > buf.Length) return false;
        }
        path = new string(buf, 0, (int)len);
        return path.Length > 0;
    }

    private const ushort ATTR_BIT_MAP_COUNT = 5;
    private const uint ATTR_CMN_FLAGS = 0x00040000u;
    private const uint ATTR_CMN_FULLPATH = 0x08000000u;
    private const uint FSOPT_NOFOLLOW = 0x00000001u;
    private const uint SF_DATALESS = 0x40000000u;
    private const int O_RDONLY = 0x0000;
    private const int O_NOFOLLOW = 0x0100;
    private const int O_DIRECTORY = 0x00100000;
    private const int IOPOL_TYPE_VFS_MATERIALIZE_DATALESS_FILES = 3;
    private const int IOPOL_SCOPE_THREAD = 1;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_DEFAULT = 0;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_OFF = 1;
    private const int IOPOL_MATERIALIZE_DATALESS_FILES_ON = 2;
    private const uint FILE_SHARE_ALL = 0x00000007u;
    private const uint OPEN_EXISTING = 3u;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000u;

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAttrList
    {
        public ushort bitmapcount;
        public ushort reserved;
        public uint commonattr;
        public uint volattr;
        public uint dirattr;
        public uint fileattr;
        public uint forkattr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinFlagsAttrBuf
    {
        public uint length;
        public uint flags;
    }

    [DllImport("libSystem.dylib", EntryPoint = "getattrlist")]
    private static extern int DarwinGetAttrList(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref DarwinAttrList attrList,
        out DarwinFlagsAttrBuf attrBuf,
        nuint attrBufSize,
        uint options);

    [DllImport("libSystem.dylib", EntryPoint = "fgetattrlist")]
    private static extern int DarwinFGetAttrList(
        int fd,
        ref DarwinAttrList attrList,
        [Out] byte[] attrBuf,
        nuint attrBufSize,
        uint options);

    [DllImport("libSystem.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int DarwinOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag);

    [DllImport("libSystem.dylib", EntryPoint = "openat", SetLastError = true)]
    private static extern int DarwinOpenAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag);

    [DllImport("libSystem.dylib", EntryPoint = "close")]
    private static extern int DarwinClose(int fd);

    [DllImport("libSystem.dylib", EntryPoint = "getiopolicy_np")]
    private static extern int DarwinGetIoPolicy(int iotype, int scope);

    [DllImport("libSystem.dylib", EntryPoint = "setiopolicy_np")]
    private static extern int DarwinSetIoPolicy(int iotype, int scope, int policy);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle WinCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WinGetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

}
