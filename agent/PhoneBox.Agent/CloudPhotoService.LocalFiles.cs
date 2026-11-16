using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed partial class CloudPhotoService
{
    private static readonly string[] BroadUnixRoots =
    [
        "/", "/Users", "/home", "/Volumes", "/mnt", "/media", "/tmp", "/var",
        "/etc", "/usr", "/System", "/Library", "/Applications", "/opt", "/bin", "/sbin", "/private"
    ];

    // ---------------------------------------------------------------- local & desktop-synced folders

    private (string SourceLabel, List<ScannedPhoto> Items, int CloudOnlyCount) FetchFromLocalDirectory(
        PhotoWidgetSettings s,
        string rawFolder,
        string fallbackLabel)
    {
        if (IsOverlyBroadLocalFolder(rawFolder))
            throw new BroadFolderException(BroadFolderMessage);

        var expanded = ExpandUserPath(rawFolder);
        if (string.IsNullOrWhiteSpace(expanded))
            throw new IOException("Choose a photo folder on this computer");

        var rootFull = TryResolveSafeDirectory(expanded)
            ?? throw new IOException("Selected folder was not found or is a symlink");

        if (IsOverlyBroadLocalFolder(rootFull))
            throw new BroadFolderException(BroadFolderMessage);

        var rootPrefix = EnsureTrailingSep(rootFull);
        var label = Path.GetFileName(rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(label)) label = fallbackLabel;

        var items = new List<ScannedPhoto>();
        var dirs = new Queue<string>();
        dirs.Enqueue(rootFull);
        var inspected = 0;
        var cloudOnlySkipped = 0;
        var cloudOnlyIncluded = 0;

        while (dirs.Count > 0 && items.Count < MaxManifestItems && inspected < MaxLocalScanEntries)
        {
            var dir = dirs.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++inspected > MaxLocalScanEntries || items.Count >= MaxManifestItems) break;

                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                FileSystemInfo fsi;
                try
                {
                    fsi = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                }
                catch { continue; }

                if (fsi.LinkTarget is not null)
                    continue;

                var fullEntry = Path.GetFullPath(entry);
                if (!IsPathInsideRoot(fullEntry, rootFull, rootPrefix))
                    continue;

                if (fsi is DirectoryInfo)
                {
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (s.Subfolders && !name.EndsWith(".photoslibrary", StringComparison.OrdinalIgnoreCase))
                        dirs.Enqueue(fullEntry);
                    continue;
                }

                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0
                    && !(OperatingSystem.IsWindows() && IsWindowsCloudPlaceholder(fsi.Attributes)))
                {
                    continue;
                }

                var ext = Path.GetExtension(name);
                if (!ImageExtensions.Contains(ext)) continue;

                var fileInfo = (FileInfo)fsi;
                if (fileInfo.Length is <= 0 or > MaxImageBytes) continue;

                var isCloudOnly = IsCloudOnlyPlaceholder(fileInfo);
                if (isCloudOnly && !s.DownloadCloudFiles)
                {
                    cloudOnlySkipped++;
                    continue;
                }
                if (isCloudOnly) cloudOnlyIncluded++;

                var takenAt = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var albumName = PathsEqual(Path.GetDirectoryName(fullEntry), rootFull)
                    ? label
                    : Path.GetFileName(Path.GetDirectoryName(fullEntry) ?? label);
                var caption = Path.GetFileNameWithoutExtension(name);
                var id = ComputeId("local", fullEntry, $"{takenAt.ToString(CultureInfo.InvariantCulture)}:{(s.DownloadCloudFiles ? "dl1" : "dl0")}");
                items.Add(new ScannedPhoto(
                    Id: id,
                    LocalFilePath: fullEntry,
                    LocalRootPath: rootFull,
                    TakenAtMs: takenAt,
                    Caption: caption,
                    Album: albumName,
                    SourceLabel: label,
                    AllowCloudDownload: s.DownloadCloudFiles,
                    IsCloudOnly: isCloudOnly));
            }
        }

        return (label, items, s.DownloadCloudFiles ? cloudOnlyIncluded : cloudOnlySkipped);
    }

    private PhotoBrowseResponse BrowseLocal(string? rawPath)
    {
        var roots = DiscoverLocalRoots();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new PhotoBrowseResponse(
                Ok: true,
                Error: null,
                Provider: "local",
                CurrentPath: null,
                ParentPath: null,
                Entries: roots.Select(r => new PhotoBrowseEntry(r.Path, r.Name, "folder", null, r.Path)).ToList());
        }

        if (IsOverlyBroadLocalFolder(rawPath))
            throw new BroadFolderException(BroadFolderMessage);

        var expanded = ExpandUserPath(rawPath);
        var full = TryResolveSafeDirectory(expanded)
            ?? throw new IOException("Selected folder does not exist or cannot be browsed");

        if (IsOverlyBroadLocalFolder(full))
            throw new BroadFolderException(BroadFolderMessage);

        var entries = new List<PhotoBrowseEntry>();
        foreach (var dir in Directory.EnumerateDirectories(full).Take(200))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            var di = new DirectoryInfo(dir);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null) continue;
            entries.Add(new PhotoBrowseEntry(di.FullName, name, "folder", null, di.FullName));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        var parent = Directory.GetParent(full)?.FullName;
        if (IsOverlyBroadLocalFolder(parent)) parent = null;
        return new PhotoBrowseResponse(true, null, "local", full, parent, entries);
    }

    private async Task<(byte[] Bytes, string Mime)?> ReadSafeLocalImageAsync(
        RegisteredPhoto item,
        CancellationToken ct)
    {
        var rootFull = TryResolveSafeDirectory(item.LocalRootPath);
        if (rootFull is null || IsOverlyBroadLocalFolder(rootFull)) return null;

        var fullFile = Path.GetFullPath(item.LocalFilePath);
        if (!IsPathInsideRoot(fullFile, rootFull, EnsureTrailingSep(rootFull)))
            return null;
        if (!HasNoIntermediateReparsePoints(rootFull, fullFile))
            return null;

        var fi = new FileInfo(fullFile);
        if (!fi.Exists || fi.LinkTarget is not null)
            return null;
        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0
            && !(OperatingSystem.IsWindows() && IsWindowsCloudPlaceholder(fi.Attributes)))
            return null;
        if (fi.Length is <= 0 or > MaxImageBytes)
            return null;
        if (IsCloudOnlyPlaceholder(fi) && !item.AllowCloudDownload)
            return null;

        var cachedPath = Path.Combine(_cacheDir, item.Id + ".bin");
        if (item.IsCloudOnly && item.AllowCloudDownload && File.Exists(cachedPath))
        {
            try
            {
                var cInfo = new FileInfo(cachedPath);
                if (cInfo.Length is > 0 and <= MaxImageBytes)
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachedPath, ct);
                    var cachedMime = DetectRasterMime(cachedBytes);
                    if (cachedMime is not null)
                    {
                        try { File.SetLastWriteTimeUtc(cachedPath, DateTime.UtcNow); } catch { }
                        return (cachedBytes, cachedMime);
                    }
                }
            }
            catch { /* fall through to source read */ }
        }

        byte[]? maybeBytes;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_bodyReadTimeout);
        try
        {
            if (_localFileReader is not null)
            {
                var readTask = Task.Run(() => _localFileReader(fullFile, item.AllowCloudDownload));
                _ = readTask.ContinueWith(static t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                maybeBytes = await readTask.WaitAsync(readCts.Token);
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (item.AllowCloudDownload)
                {
                    await _materializationGate.WaitAsync(readCts.Token);
                    var releasedByWorker = false;
                    Task<byte[]?> readTask;
                    try
                    {
                        readTask = Task.Run(() =>
                        {
                            try
                            {
                                return ReadMacFileWithPolicy(rootFull, fullFile, IOPOL_MATERIALIZE_DATALESS_FILES_ON);
                            }
                            finally
                            {
                                ReleaseGateQuietly(_materializationGate);
                            }
                        });
                        releasedByWorker = true;
                    }
                    finally
                    {
                        if (!releasedByWorker)
                            ReleaseGateQuietly(_materializationGate);
                    }
                    _ = readTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    maybeBytes = await readTask.WaitAsync(readCts.Token);
                }
                else
                {
                    var readTask = Task.Run(() => ReadMacFileWithPolicy(rootFull, fullFile, IOPOL_MATERIALIZE_DATALESS_FILES_OFF));
                    _ = readTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    maybeBytes = await readTask.WaitAsync(readCts.Token);
                }
            }
            else
            {
                if (OperatingSystem.IsWindows() && !item.AllowCloudDownload)
                {
                    // Re-check right before opening so newly dehydrated OneDrive placeholders are skipped.
                    if (IsWindowsCloudPlaceholder(File.GetAttributes(fullFile)))
                        return null;
                }
                await using var fs = new FileStream(fullFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16384, useAsync: true);
                if (!IsOpenHandleInsideRoot(fs.SafeFileHandle, rootFull))
                    return null;
                maybeBytes = await ReadBoundedFileStreamAsync(fs, readCts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }

        if (maybeBytes is null) return null;
        var mime = DetectRasterMime(maybeBytes);
        if (mime is null) return null;

        if (item.IsCloudOnly && item.AllowCloudDownload)
            await WriteCacheFileAsync(cachedPath, maybeBytes, ct);

        return (maybeBytes, mime);
    }

    private static void ReleaseGateQuietly(SemaphoreSlim gate)
    {
        try { gate.Release(); } catch (ObjectDisposedException) { }
    }


    // ---------------------------------------------------------------- private disk cache (0700 dir / 0600 files on Unix)

    internal async Task WriteCacheFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            AppPaths.CreateDirectoryPrivate(_cacheDir);
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var fs = AppPaths.CreatePrivate(tmp))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
            }
            File.Move(tmp, path, overwrite: true);
            AppPaths.RestrictToOwner(path);
            tmp = null;

            if (Interlocked.Increment(ref _cacheWriteCounter) % 8 == 0)
                PruneCacheDirectory();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { /* disk cache failures are non-fatal */ }
        finally
        {
            try
            {
                if (tmp is not null && File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch { }
        }
    }

    internal void PruneCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            var maxBytes = Math.Clamp(_config.Get().CloudPhotos.CacheMaxMb, 20, 2048) * 1024L * 1024L;
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*.bin")
                .Select(f => new { f.FullName, f.Length, f.LastWriteTimeUtc })
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            var target = (long)(maxBytes * 0.85);
            foreach (var f in files)
            {
                if (total <= target) break;
                try
                {
                    File.Delete(f.FullName);
                    total -= f.Length;
                }
                catch { }
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- validation & utilities

    /// <summary>Inspects leading magic bytes so only safe raster images are served (never SVG/HTML/scripts).</summary>
    public static string? DetectRasterMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        if (bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
            return "image/gif";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return "image/bmp";
        return null;
    }

    private const string BroadFolderMessage =
        "Choose a specific photo folder or album subfolder rather than a drive root or home folder";

    internal static bool IsSourceConfigured(PhotoWidgetSettings s) =>
        !string.IsNullOrWhiteSpace(s.Folder);

    /// <summary>
    /// Rejects drive roots, top-level OS/user directories, and the user's home root itself so a misconfigured widget
    /// cannot scan an entire volume or profile.
    /// </summary>
    public static bool IsOverlyBroadLocalFolder(string? rawFolder)
    {
        if (string.IsNullOrWhiteSpace(rawFolder)) return false;
        var trimmed = rawFolder.Trim();
        if (trimmed is "/" or "\\" or "~" or "~/" or "~\\") return true;
        if (WindowsBroadPathRegex().IsMatch(trimmed)) return true;

        var expanded = ExpandUserPath(trimmed);
        var unixTrimmed = trimmed.Replace('\\', '/').TrimEnd('/');
        if (unixTrimmed.Length == 0) return true;
        foreach (var root in BroadUnixRoots)
        {
            if (root != "/" && string.Equals(unixTrimmed, root, StringComparison.Ordinal))
                return true;
        }

        string full;
        try { full = Path.GetFullPath(expanded); }
        catch { return false; }

        var fullRoot = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(fullRoot) && PathsEqual(TrimDirSep(full), TrimDirSep(fullRoot)))
            return true;

        var normFull = TrimDirSep(full);
        foreach (var root in BroadUnixRoots)
        {
            if (PathsEqual(normFull, root == "/" ? "/" : root))
                return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var normHome = TrimDirSep(Path.GetFullPath(home));
            if (PathsEqual(normFull, normHome)) return true;
            var homeParent = Path.GetDirectoryName(normHome);
            if (!string.IsNullOrWhiteSpace(homeParent) && PathsEqual(normFull, TrimDirSep(homeParent)))
                return true;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var sf in new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            })
            {
                var p = Environment.GetFolderPath(sf, Environment.SpecialFolderOption.DoNotVerify);
                if (!string.IsNullOrWhiteSpace(p) && PathsEqual(normFull, TrimDirSep(Path.GetFullPath(p))))
                    return true;
            }
        }

        return false;
    }

    private static string TrimDirSep(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    private static string SourceKey(PhotoWidgetSettings s) =>
        $"{s.Provider}|{s.Folder}|{(s.Subfolders ? '1' : '0')}|{(s.DownloadCloudFiles ? '1' : '0')}";

    private static string ComputeId(string provider, string key, string stamp)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\0{key}\0{stamp}"));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ExpandUserPath(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            return Path.Combine(home, trimmed[2..]);
        }
        return trimmed;
    }

    private static string? TryResolveSafeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return null;
            var di = new DirectoryInfo(full);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null)
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string EnsureTrailingSep(string fullPath) =>
        fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;

    private static string NormalizeMacPath(string path) =>
        OperatingSystem.IsMacOS() ? path.Normalize(NormalizationForm.FormC) : path;

    internal static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(NormalizeMacPath(a), NormalizeMacPath(b), PathComparison);

    internal static bool IsPathInsideRoot(string fullCandidate, string rootFull, string rootWithSep)
    {
        var cand = NormalizeMacPath(fullCandidate);
        var root = NormalizeMacPath(rootFull);
        var pref = NormalizeMacPath(rootWithSep);
        return string.Equals(cand, root, PathComparison) || cand.StartsWith(pref, PathComparison);
    }

    private static void AddGlobCloudStorage(
        List<(string Id, string Name, string Hint, string? Path)> candidates,
        string parentDir,
        string pattern,
        string hint,
        string label)
    {
        try
        {
            if (!Directory.Exists(parentDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(parentDir, pattern).Take(4))
            {
                var leaf = Path.GetFileName(dir);
                candidates.Add(($"cs-{hint}-{leaf}", $"{label} ({leaf})", hint, dir));
            }
        }
        catch { }
    }

    private static void TryAddDropboxJsonRoots(
        List<(string Id, string Name, string Hint, string? Path)> candidates,
        string home)
    {
        try
        {
            var infoPaths = new[]
            {
                Path.Combine(home, ".dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
            };
            foreach (var infoPath in infoPaths)
            {
                if (!File.Exists(infoPath)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                foreach (var kind in new[] { "personal", "business" })
                {
                    if (doc.RootElement.TryGetProperty(kind, out var section)
                        && section.TryGetProperty("path", out var pEl)
                        && pEl.GetString() is { Length: > 0 } p)
                    {
                        candidates.Add(($"db-json-{kind}", $"Dropbox ({kind})", "dropbox", p));
                    }
                }
            }
        }
        catch { }
    }

    [GeneratedRegex(@"^[A-Za-z]:(?:[\\/](?:Users|Windows|Program Files(?: \(x86\))?|ProgramData)?[\\/]?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsBroadPathRegex();

}
