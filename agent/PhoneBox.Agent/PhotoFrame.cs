using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>Settings › General › Photo frame. The folder stays on this computer (a local path names the user).</summary>
public sealed class PhotoFrameConfig
{
    public string Folder { get; set; } = "";
    public bool Subfolders { get; set; } = true;
}

/// <summary>
/// Photos for the Photo frame widget from one folder on this computer (a synced OneDrive, Dropbox or iCloud folder
/// works too). The phone gets ids and asks for one photo at a time; large photos are scaled down here first, so an old
/// phone never decodes a camera original. Nothing is scanned or served while no enabled page shows the widget, and
/// files that are only in the cloud are skipped, so a slideshow never downloads a whole synced library.
/// </summary>
public sealed class PhotoFrame(ConfigStore config, ILogger<PhotoFrame> log)
{
    internal const string WidgetType = "photos";
    internal const int MaxFiles = 5000, LongEdge = 1600;
    internal const long MaxSourceBytes = 60L * 1024 * 1024;
    private const long OriginalMaxBytes = 6L * 1024 * 1024;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMinutes(5);
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    internal sealed record Photo(string Id, string Path, long Length, long Ticks);
    internal sealed record Library(string Root, bool Subfolders, IReadOnlyList<Photo> Photos, long ScannedAt, string? Error);

    private readonly Lock _gate = new();
    private Library? _library;
    private Task? _scan;
    private readonly SemaphoreSlim _convert = new(1, 1);

    /// <summary>Test seam: where scaled copies are kept.</summary>
    internal string CacheDir { get; init; } = System.IO.Path.Combine(AppPaths.LocalDir, "photos-cache");
    /// <summary>Test seam: the clock (Environment.TickCount64).</summary>
    internal Func<long> Now { get; init; } = () => Environment.TickCount64;

    /// <summary>Test seam: scans now and waits for it.</summary>
    internal void ScanNow() { lock (_gate) _library = null; Current(config.Get().Photos, wait: true); }

    public bool Shown() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    /// <summary>The photo ids, or an error the phone can show (never a local path).</summary>
    public (IReadOnlyList<string> Ids, string? Error, bool Scanning) List()
    {
        if (!Shown()) return ([], "Add a Photo frame widget to a page to show photos", false);
        var cfg = config.Get().Photos;
        if (string.IsNullOrWhiteSpace(cfg.Folder)) return ([], null, false);
        var lib = Current(cfg, wait: false);
        if (lib is null) return ([], null, true);
        return (lib.Photos.Select(p => p.Id).ToList(), lib.Error, false);
    }

    /// <summary>The library for these settings; scans in the background when it is missing or older than five minutes.</summary>
    private Library? Current(PhotoFrameConfig cfg, bool wait)
    {
        Task? scan;
        lock (_gate)
        {
            var root = cfg.Folder.Trim();
            var lib = _library;
            var fresh = lib is not null && lib.Root == root && lib.Subfolders == cfg.Subfolders;
            if (fresh && Now() - lib!.ScannedAt < ScanEvery.TotalMilliseconds) return lib;
            _scan ??= Task.Run(() =>
            {
                var next = Scan(root, cfg.Subfolders);
                lock (_gate) { _library = next with { ScannedAt = Now() }; _scan = null; }
            });
            scan = _scan;
            if (fresh && !wait) return lib; // the old list until the new scan is done
        }
        if (!wait) return null;
        scan.Wait(TimeSpan.FromSeconds(30));
        lock (_gate) return _library;
    }

    /// <summary>Lists the images in the folder: bounded, no hidden or system files, no links, nothing that is only in the cloud.</summary>
    internal static Library Scan(string root, bool subfolders)
    {
        if (RefuseRoot(root) is { } refused) return new(root, subfolders, [], 0, refused);
        var photos = new List<Photo>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = subfolders, MaxRecursionDepth = 6, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline
                    | RecallOnOpen | RecallOnDataAccess,
            };
            var full = System.IO.Path.GetFullPath(root);
            foreach (var f in new DirectoryInfo(full).EnumerateFiles("*", options))
            {
                if (!Extensions.Contains(f.Extension.ToLowerInvariant()) || CloudOnly(f.Attributes) || f.LinkTarget is not null) continue;
                if (f.Length is <= 0 or > MaxSourceBytes) continue;
                var rel = System.IO.Path.GetRelativePath(full, f.FullName);
                photos.Add(new Photo(IdOf(rel), f.FullName, f.Length, f.LastWriteTimeUtc.Ticks));
                if (photos.Count >= MaxFiles) break;
            }
            photos.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return new(root, subfolders, photos, 0, photos.Count == 0 ? "No photos in the folder" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(root, subfolders, [], 0, "The photo folder can't be read");
        }
    }

    // Windows cloud placeholders (OneDrive Files On-Demand and others): reading them downloads the file.
    internal const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    internal const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    internal static bool CloudOnly(FileAttributes a) => (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    internal static string IdOf(string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/'))))[..16];

    /// <summary>Folders too broad to show as a photo frame: a drive or file-system root, the home folder itself, system folders.</summary>
    internal static string? RefuseRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root)) return "Choose a full folder path in Settings › General";
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "Network paths aren't supported; use a folder on this computer";
        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Choose a full folder path in Settings › General"; }
        var home = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Same(string? other) => !string.IsNullOrEmpty(other) && string.Equals(full, System.IO.Path.TrimEndingDirectorySeparator(other), cmp);
        bool Under(string? other) => !string.IsNullOrEmpty(other)
            && (Same(other) || full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(other) + System.IO.Path.DirectorySeparatorChar, cmp));
        if (System.IO.Path.GetPathRoot(full) is { } r && Same(r)) return "Choose a photo folder, not a whole drive";
        if (Same(home) || Same(System.IO.Path.GetDirectoryName(home))) return "Choose a photo folder, not your whole home folder";
        if (Under(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) || Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) || Under(AppPaths.ConfigDir)
            || (!OperatingSystem.IsWindows() && (Under("/etc") || Under("/proc") || Under("/sys") || Under("/dev") || Under("/System") || Under("/Library") || Under("/usr"))))
            return "Choose a photo folder, not a system folder";
        if (!Directory.Exists(full)) return "The photo folder doesn't exist";
        return null;
    }

    /// <summary>One photo, scaled down when it is large; null with a reason when it can't be served.</summary>
    public async Task<(byte[]? Bytes, string? Type, string? Error)> Get(string? id, CancellationToken ct)
    {
        if (!Shown()) return (null, null, "Add a Photo frame widget to a page to show photos");
        var cfg = config.Get().Photos;
        if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(cfg.Folder)) return (null, null, "Unknown photo");
        var lib = Current(cfg, wait: false);
        if (lib?.Photos.FirstOrDefault(p => p.Id == id) is not { } photo) return (null, null, "Unknown photo");
        // checked again now: the file may have been replaced by a link, moved to the cloud or grown since the scan
        var fi = new FileInfo(photo.Path);
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(lib.Root)) + System.IO.Path.DirectorySeparatorChar;
        if (!fi.Exists || !fi.FullName.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || fi.LinkTarget is not null || (fi.Attributes & FileAttributes.ReparsePoint) != 0 || CloudOnly(fi.Attributes)
            || fi.Length is <= 0 or > MaxSourceBytes)
            return (null, null, "That photo is no longer available");
        try { return await Prepare(photo with { Length = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks }, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            log.LogDebug("photo {Id}: {Msg}", id, ex.Message);
            return (null, null, "That photo can't be shown");
        }
    }

    private async Task<(byte[]? Bytes, string? Type, string? Error)> Prepare(Photo photo, CancellationToken ct)
    {
        var head = new byte[12];
        await using (var s = new FileStream(photo.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct) < 4) return (null, null, "That photo can't be shown");
        if (MimeOf(head) is not { } mime) return (null, null, "That photo can't be shown");

        var cached = System.IO.Path.Combine(CacheDir, $"{photo.Id}-{photo.Ticks:x}-{photo.Length:x}.jpg");
        if (File.Exists(cached)) return (await File.ReadAllBytesAsync(cached, ct), "image/jpeg", null);

        // small enough, or a format only the phone decodes (GIF, WebP): sent as it is
        if (mime is "image/gif" or "image/webp" || photo.Length <= 400_000)
            return photo.Length <= OriginalMaxBytes ? (await File.ReadAllBytesAsync(photo.Path, ct), mime, null) : (null, null, "That photo is too large to show");

        await _convert.WaitAsync(ct);
        try
        {
            if (File.Exists(cached)) return (await File.ReadAllBytesAsync(cached, ct), "image/jpeg", null);
            if (Scale(photo.Path, ct) is { } jpeg)
            {
                Directory.CreateDirectory(CacheDir);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var old in Directory.EnumerateFiles(CacheDir, photo.Id + "-*.jpg")) File.Delete(old); // older versions of this photo
                await using (var f = AppPaths.CreatePrivate(cached)) await f.WriteAsync(jpeg, ct);
                return (jpeg, "image/jpeg", null);
            }
        }
        finally { _convert.Release(); }
        // no converter here (Linux): the original, when it isn't too large for an old phone
        return photo.Length <= OriginalMaxBytes ? (await File.ReadAllBytesAsync(photo.Path, ct), mime, null) : (null, null, "That photo is too large to show");
    }

    /// <summary>The image type from its first bytes; a file named .jpg that isn't one is refused.</summary>
    internal static string? MimeOf(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)) ? "image/gif"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    /// <summary>A JPEG whose long edge is at most <see cref="LongEdge"/>, turned upright; null where no converter is available.</summary>
    private static byte[]? Scale(string path, CancellationToken ct)
    {
#if WINDOWS
        using var src = System.Drawing.Image.FromFile(path);
        // the EXIF orientation is lost when re-encoding, so it is applied to the pixels
        if (src.PropertyIdList.Contains(0x0112))
        {
            var o = src.GetPropertyItem(0x0112)?.Value is { Length: > 0 } v ? v[0] : 1;
            var flip = o switch
            {
                2 => System.Drawing.RotateFlipType.RotateNoneFlipX, 3 => System.Drawing.RotateFlipType.Rotate180FlipNone,
                4 => System.Drawing.RotateFlipType.Rotate180FlipX, 5 => System.Drawing.RotateFlipType.Rotate90FlipX,
                6 => System.Drawing.RotateFlipType.Rotate90FlipNone, 7 => System.Drawing.RotateFlipType.Rotate270FlipX,
                8 => System.Drawing.RotateFlipType.Rotate270FlipNone, _ => System.Drawing.RotateFlipType.RotateNoneFlipNone,
            };
            src.RotateFlip(flip);
        }
        var scale = Math.Min(1.0, (double)LongEdge / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(System.Drawing.Color.Black);
            g.DrawImage(src, 0, 0, w, h);
        }
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var ps = new System.Drawing.Imaging.EncoderParameters(1);
        ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, ps);
        return ms.ToArray();
#else
        if (!OperatingSystem.IsMacOS()) return null;
        // sips keeps the EXIF orientation, which the WebView honours
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phonebox-photo-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sips") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-Z", LongEdge.ToString(System.Globalization.CultureInfo.InvariantCulture), "-s", "format", "jpeg", "-s", "formatOptions", "85", path, "--out", tmp })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(20_000)) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            return p.ExitCode == 0 && File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
        }
        finally { try { File.Delete(tmp); } catch { } }
#endif
    }
}
