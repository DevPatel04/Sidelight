using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

/// <summary>
/// Discovers, browses, validates, and serves photos from local and desktop-synced folders on demand
/// (zero background polling, zero OAuth, zero outbound HTTP).
/// </summary>
public sealed partial class CloudPhotoService : IDisposable
{
    public const long MaxImageBytes = 25 * 1024 * 1024;
    private const int CacheTtlMs = 15 * 60 * 1000;
    private const int MaxManifestItems = 250;
    private const int MaxItemRegistry = 5000;
    private const int MaxLocalScanEntries = 4000;
    private const int LanRefreshWidgetCooldownMs = 15_000;
    private const int LanRefreshDeviceWindowMs = 30_000;
    private const int LanRefreshDeviceMaxPerWindow = 5;

    private sealed class BroadFolderException(string message) : IOException(message);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    internal sealed record CachedManifest(
        PhotoFeedResponse Response,
        long FetchedAtMs
    );

    private sealed record InFlightRefresh(
        Task<PhotoFeedResponse> Task,
        CancellationTokenSource CancelCts
    );

    internal sealed class RegisteredPhoto
    {
        public required string Id { get; init; }
        public required string LocalFilePath { get; init; }
        public required string LocalRootPath { get; init; }
        public long? TakenAtMs { get; init; }
        public string? Caption { get; init; }
        public string? Album { get; init; }
        public string? SourceLabel { get; init; }
        public bool AllowCloudDownload { get; init; }
        public bool IsCloudOnly { get; init; }
        public bool AllowPreview { get; set; }
        public HashSet<string> SourceKeys { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ScannedPhoto(
        string Id,
        string LocalFilePath,
        string LocalRootPath,
        long? TakenAtMs,
        string? Caption,
        string? Album,
        string? SourceLabel,
        bool AllowCloudDownload,
        bool IsCloudOnly
    );

    private readonly ConfigStore _config;
    private readonly string _cacheDir;
    private readonly IReadOnlyList<PhotoLocalRoot>? _customLocalRoots;
    private readonly Func<FileInfo, bool>? _isCloudOnlyOverride;
    private readonly Func<string, bool, byte[]>? _localFileReader;
    private readonly TimeSpan _bodyReadTimeout;
    private readonly Func<long> _nowMs;
    private readonly SemaphoreSlim _materializationGate = new(2, 2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlightRefresh> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredPhoto> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _itemOrder = new();
    private readonly Dictionary<string, long> _lanWidgetRefreshAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> _lanDeviceRefreshHistory = new(StringComparer.Ordinal);
    private int _cacheWriteCounter;

    public CloudPhotoService(ConfigStore config)
        : this(config, Path.Combine(AppPaths.LocalDir, "photos-cache")) { }

    internal CloudPhotoService(
        ConfigStore config,
        string cacheDir,
        IEnumerable<PhotoLocalRoot>? customLocalRoots = null,
        Func<FileInfo, bool>? isCloudOnlyOverride = null,
        Func<string, bool, byte[]>? localFileReader = null,
        TimeSpan? bodyReadTimeout = null,
        Func<long>? nowMsProvider = null)
    {
        _config = config;
        _cacheDir = cacheDir;
        _customLocalRoots = customLocalRoots?.ToList();
        _isCloudOnlyOverride = isCloudOnlyOverride;
        _localFileReader = localFileReader;
        _bodyReadTimeout = bodyReadTimeout ?? TimeSpan.FromSeconds(25);
        _nowMs = nowMsProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private long NowMs => _nowMs();

    public PhotoStatusResponse GetStatus() => new(DiscoverLocalRoots());

    public IReadOnlyList<PhotoLocalRoot> DiscoverLocalRoots()
    {
        if (_customLocalRoots is not null)
            return _customLocalRoots;

        var candidates = new List<(string Id, string Name, string Hint, string? Path)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);

        candidates.Add(("pictures", "Pictures", "local", pictures));

        // iCloud Photos (local desktop sync folders)
        if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("icloud-drive-photos", "iCloud Drive › Photos", "icloud", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs", "Photos")));
            candidates.Add(("icloud-pics", "Pictures › iCloud Photos", "icloud", Path.Combine(pictures, "iCloud Photos")));
        }
        else
        {
            candidates.Add(("icloud-photos", "iCloud Photos", "icloud", Path.Combine(pictures, "iCloud Photos", "Photos")));
            candidates.Add(("icloud-root", "iCloud Photos folder", "icloud", Path.Combine(pictures, "iCloud Photos")));
            candidates.Add(("icloud-drive", "iCloud Drive › Photos", "icloud", Path.Combine(home, "iCloudDrive", "Photos")));
        }

        // OneDrive (local desktop sync folders)
        foreach (var envName in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var envDir = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                candidates.Add(($"od-{envName}-pics", "OneDrive › Pictures", "onedrive", Path.Combine(envDir, "Pictures")));
                candidates.Add(($"od-{envName}", "OneDrive", "onedrive", envDir));
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            AddGlobCloudStorage(candidates, Path.Combine(home, "Library", "CloudStorage"), "OneDrive*", "onedrive", "OneDrive");
            AddGlobCloudStorage(candidates, Path.Combine(home, "Library", "CloudStorage"), "GoogleDrive*", "google", "Google Drive");
        }
        candidates.Add(("od-home-pics", "OneDrive › Pictures", "onedrive", Path.Combine(home, "OneDrive", "Pictures")));
        candidates.Add(("od-home", "OneDrive", "onedrive", Path.Combine(home, "OneDrive")));

        // Google Drive (local desktop sync folders)
        candidates.Add(("gd-home-my", "Google Drive › My Drive", "google", Path.Combine(home, "Google Drive", "My Drive")));
        candidates.Add(("gd-home", "Google Drive", "google", Path.Combine(home, "Google Drive")));

        // Dropbox (~/.dropbox/info.json first, then common desktop sync paths)
        TryAddDropboxJsonRoots(candidates, home);
        candidates.Add(("db-cam", "Dropbox › Camera Uploads", "dropbox", Path.Combine(home, "Dropbox", "Camera Uploads")));
        candidates.Add(("db-photos", "Dropbox › Photos", "dropbox", Path.Combine(home, "Dropbox", "Photos")));
        candidates.Add(("db-root", "Dropbox", "dropbox", Path.Combine(home, "Dropbox")));
        if (OperatingSystem.IsMacOS())
            candidates.Add(("db-cs", "Dropbox (CloudStorage)", "dropbox", Path.Combine(home, "Library", "CloudStorage", "Dropbox")));

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<PhotoLocalRoot>();
        foreach (var (id, name, hint, rawPath) in candidates)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || IsOverlyBroadLocalFolder(rawPath)) continue;
            var actual = TryResolveSafeDirectory(rawPath);
            if (actual is null || IsOverlyBroadLocalFolder(actual)) continue;
            if (seen.Add(actual))
                result.Add(new PhotoLocalRoot(id, name, hint, actual));

            if (hint == "google" && !actual.EndsWith("My Drive", StringComparison.OrdinalIgnoreCase))
            {
                var myDrivePath = Path.Combine(actual, "My Drive");
                var myDrive = !IsOverlyBroadLocalFolder(myDrivePath) ? TryResolveSafeDirectory(myDrivePath) : null;
                if (myDrive is not null && seen.Add(myDrive))
                    result.Add(new PhotoLocalRoot(id + "-mydrive", name + " › My Drive", hint, myDrive));
            }
        }
        return result;
    }

    public bool TryConsumeLanRefresh(string deviceId, string widgetId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var now = NowMs;
        var pairKey = $"{deviceId}\0{widgetId}";

        lock (_gate)
        {
            if (_lanWidgetRefreshAt.Count > 256)
            {
                var stale = _lanWidgetRefreshAt.Where(kv => now - kv.Value >= LanRefreshWidgetCooldownMs).Select(kv => kv.Key).ToList();
                foreach (var k in stale) _lanWidgetRefreshAt.Remove(k);
            }
            if (_lanDeviceRefreshHistory.Count > 128)
            {
                var staleDev = _lanDeviceRefreshHistory
                    .Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() >= LanRefreshDeviceWindowMs)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in staleDev) _lanDeviceRefreshHistory.Remove(k);
            }

            if (_lanWidgetRefreshAt.TryGetValue(pairKey, out var lastWidgetAt)
                && now - lastWidgetAt < LanRefreshWidgetCooldownMs)
            {
                return false;
            }

            if (!_lanDeviceRefreshHistory.TryGetValue(deviceId, out var q))
            {
                q = new Queue<long>();
                _lanDeviceRefreshHistory[deviceId] = q;
            }
            while (q.Count > 0 && now - q.Peek() >= LanRefreshDeviceWindowMs)
                q.Dequeue();

            if (q.Count >= LanRefreshDeviceMaxPerWindow)
                return false;

            _lanWidgetRefreshAt[pairKey] = now;
            q.Enqueue(now);
            return true;
        }
    }

    public bool HasAnyAuthorizedPhotosWidget(string? deviceId)
    {
        var cfg = _config.Get();
        foreach (var page in cfg.PagesFor(deviceId).Where(p => p.Enabled))
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "photos") return true;
                if (w.Type == "stack" && StackWidgets.Children(w).Any(c => c.Enabled && c.Type == "photos"))
                    return true;
            }
        }
        return false;
    }

    public PhotoWidgetSettings? FindWidgetSettings(string? widgetId, string? deviceId = null, bool enabledPagesOnly = true)
    {
        if (string.IsNullOrWhiteSpace(widgetId)) return null;
        var cfg = _config.Get();
        var pages = cfg.PagesFor(deviceId).Where(p => !enabledPagesOnly || p.Enabled).ToList();

        foreach (var page in pages)
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "photos" && string.Equals(w.Id, widgetId, StringComparison.Ordinal))
                    return PhotoWidgetOptions.Parse(w.Options);
            }
        }

        var colon = widgetId.IndexOf(':');
        if (colon > 0)
        {
            var stackId = widgetId[..colon];
            var childId = widgetId[(colon + 1)..];
            foreach (var page in pages)
            {
                foreach (var w in page.Widgets ?? [])
                {
                    if (w.Type == "stack" && string.Equals(w.Id, stackId, StringComparison.Ordinal))
                    {
                        var child = StackWidgets.Children(w)
                            .FirstOrDefault(c => c.Enabled && c.Type == "photos" && string.Equals(c.Id, childId, StringComparison.Ordinal));
                        if (child is not null)
                            return PhotoWidgetOptions.Parse(child.Options);
                    }
                }
            }
        }

        return null;
    }

    public Task<PhotoFeedResponse> GetWidgetFeedAsync(
        string widgetId,
        bool refresh,
        CancellationToken ct,
        string? deviceId = null,
        bool sanitizeErrors = true)
    {
        var settings = FindWidgetSettings(widgetId, deviceId, enabledPagesOnly: true);
        if (settings is null)
        {
            return Task.FromResult(new PhotoFeedResponse(
                false, "local", "", null, NowMs, false, "Photo frame widget was not found", []));
        }
        return GetFeedAsync(settings, refresh, forPreview: false, sanitizeErrors: sanitizeErrors, ct);
    }

    public Task<PhotoFeedResponse> GetPreviewFeedAsync(PhotoPreviewRequest req, CancellationToken ct)
    {
        var settings = PhotoWidgetOptions.Parse(req.Options);
        return GetFeedAsync(settings, refresh: true, forPreview: true, sanitizeErrors: false, ct);
    }

    public async Task<PhotoTestResponse> TestSourceAsync(PhotoTestRequest req, CancellationToken ct)
    {
        var settings = PhotoWidgetOptions.Parse(req.Options);
        if (!IsSourceConfigured(settings))
            return new PhotoTestResponse(false, "Choose a photo folder in PhoneBox Settings", 0, "");

        if (IsOverlyBroadLocalFolder(settings.Folder))
            return new PhotoTestResponse(false, BroadFolderMessage, 0, "");

        var feed = await GetFeedAsync(settings, refresh: true, forPreview: true, sanitizeErrors: false, ct);
        if (!settings.DownloadCloudFiles && feed.Photos.Count == 0 && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                false,
                $"Found {feed.CloudOnlyCount} cloud-only photo{(feed.CloudOnlyCount == 1 ? "" : "s")} in {feed.SourceLabel}. Enable \"Download cloud-only files on demand\" below — viewing them will download photos from your cloud storage onto this computer.",
                0,
                feed.SourceLabel,
                feed.CloudOnlyCount);
        }
        if (!string.IsNullOrEmpty(feed.Error))
            return new PhotoTestResponse(false, feed.Error, feed.Photos.Count, feed.SourceLabel, feed.CloudOnlyCount);
        if (feed.Photos.Count == 0)
            return new PhotoTestResponse(false, $"No supported photos found in {feed.SourceLabel}", 0, feed.SourceLabel);
        if (!settings.DownloadCloudFiles && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                true,
                $"Found {feed.Photos.Count} local photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel} ({feed.CloudOnlyCount} cloud-only skipped — enable on-demand download to include them)",
                feed.Photos.Count,
                feed.SourceLabel,
                feed.CloudOnlyCount);
        }
        if (settings.DownloadCloudFiles && feed.CloudOnlyCount > 0)
        {
            return new PhotoTestResponse(
                true,
                $"Found {feed.Photos.Count} photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel} ({feed.CloudOnlyCount} in cloud — photos will be downloaded onto this computer as they are shown)",
                feed.Photos.Count,
                feed.SourceLabel,
                feed.CloudOnlyCount);
        }

        return new PhotoTestResponse(true, $"Found {feed.Photos.Count} photo{(feed.Photos.Count == 1 ? "" : "s")} in {feed.SourceLabel}", feed.Photos.Count, feed.SourceLabel);
    }

    public Task<PhotoBrowseResponse> BrowseAsync(PhotoBrowseRequest req, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(BrowseLocal(req.Path));
        }
        catch (BroadFolderException ex)
        {
            return Task.FromResult(new PhotoBrowseResponse(false, ex.Message, "local", req.Path, null, []));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new PhotoBrowseResponse(false, "Selected folder could not be browsed", "local", req.Path, null, []));
        }
    }

    public async Task<(byte[] Bytes, string Mime)?> GetPhotoBytesAsync(
        string id,
        CancellationToken ct,
        string? deviceId = null,
        bool enforceDeviceScope = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80) return null;

        RegisteredPhoto? item;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out item))
                return null;
        }

        if (enforceDeviceScope || !string.IsNullOrWhiteSpace(deviceId))
        {
            var authorizedKeys = AuthorizedSourceKeys(deviceId);
            if (authorizedKeys.Count == 0) return null;
            bool allowed;
            lock (_gate)
            {
                allowed = item.SourceKeys.Any(authorizedKeys.Contains);
            }
            if (!allowed) return null;
        }
        else
        {
            var authorizedKeys = AuthorizedSourceKeys(null);
            bool allowed;
            lock (_gate)
            {
                allowed = item.AllowPreview || item.SourceKeys.Any(authorizedKeys.Contains);
            }
            if (!allowed) return null;
        }

        return await ReadSafeLocalImageAsync(item, ct);
    }

    private HashSet<string> AuthorizedSourceKeys(string? deviceId)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cfg = _config.Get();
        foreach (var page in cfg.PagesFor(deviceId).Where(p => p.Enabled))
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "photos")
                {
                    var s = PhotoWidgetOptions.Parse(w.Options);
                    if (IsSourceConfigured(s) && !IsOverlyBroadLocalFolder(s.Folder))
                        keys.Add(SourceKey(s));
                }
                else if (w.Type == "stack")
                {
                    foreach (var child in StackWidgets.Children(w).Where(c => c.Enabled && c.Type == "photos"))
                    {
                        var s = PhotoWidgetOptions.Parse(child.Options);
                        if (IsSourceConfigured(s) && !IsOverlyBroadLocalFolder(s.Folder))
                            keys.Add(SourceKey(s));
                    }
                }
            }
        }
        return keys;
    }

    private async Task<PhotoFeedResponse> GetFeedAsync(
        PhotoWidgetSettings settings,
        bool refresh,
        bool forPreview,
        bool sanitizeErrors,
        CancellationToken ct)
    {
        if (!IsSourceConfigured(settings))
        {
            return new PhotoFeedResponse(
                Configured: false,
                Provider: "local",
                SourceLabel: "Computer folder",
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: "Choose a photo folder in PhoneBox Settings",
                Photos: []);
        }

        if (IsOverlyBroadLocalFolder(settings.Folder))
        {
            return new PhotoFeedResponse(
                Configured: true,
                Provider: "local",
                SourceLabel: "Computer folder",
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: sanitizeErrors ? "Choose a specific subfolder in PhoneBox Settings" : BroadFolderMessage,
                Photos: []);
        }

        var key = SourceKey(settings);
        CachedManifest? staleFallback = null;
        InFlightRefresh? existing = null;

        lock (_gate)
        {
            if (_manifests.TryGetValue(key, out var cached))
            {
                staleFallback = cached;
                if (!refresh && !forPreview && NowMs - cached.FetchedAtMs < CacheTtlMs)
                    return ApplyPresentationOrder(cached.Response, settings);
            }

            if (!_inFlight.TryGetValue(key, out existing) || existing.Task.IsCompleted)
            {
                var cancelCts = new CancellationTokenSource();
                var task = RefreshManifestCoreAsync(key, settings, staleFallback, forPreview, sanitizeErrors, cancelCts);
                existing = new InFlightRefresh(task, cancelCts);
                if (!task.IsCompleted)
                    _inFlight[key] = existing;
            }
        }

        var fresh = await existing.Task.WaitAsync(ct);
        return ApplyPresentationOrder(fresh, settings);
    }

    private async Task<PhotoFeedResponse> RefreshManifestCoreAsync(
        string key,
        PhotoWidgetSettings settings,
        CachedManifest? staleFallback,
        bool forPreview,
        bool sanitizeErrors,
        CancellationTokenSource cancelCts)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancelCts.Token);
        var ct = linkedCts.Token;
        try
        {
            var (sourceLabel, scanned, cloudOnlyCount) = await Task.Run(
                () => FetchFromLocalDirectory(settings, settings.Folder, "Computer folder"),
                ct);
            var feedItems = new List<PhotoFeedItem>(scanned.Count);

            lock (_gate)
            {
                if (cancelCts.IsCancellationRequested)
                    throw new OperationCanceledException(cancelCts.Token);

                foreach (var sItem in scanned.Take(MaxManifestItems))
                {
                    RegisterItemLocked(sItem, key, forPreview);
                    feedItems.Add(new PhotoFeedItem(
                        Id: sItem.Id,
                        Url: "/api/photos/image/" + Uri.EscapeDataString(sItem.Id),
                        Caption: sItem.Caption,
                        TakenAt: sItem.TakenAtMs,
                        Album: sItem.Album,
                        Source: sourceLabel,
                        Width: null,
                        Height: null));
                }

                var errorMsg = !settings.DownloadCloudFiles && feedItems.Count == 0 && cloudOnlyCount > 0
                    ? $"{cloudOnlyCount} cloud-only photo{(cloudOnlyCount == 1 ? " was" : "s were")} not downloaded. Enable \"Download cloud-only files on demand\" in PhoneBox Settings to allow cloud downloads."
                    : null;

                var response = new PhotoFeedResponse(
                    Configured: true,
                    Provider: "local",
                    SourceLabel: sourceLabel,
                    Title: NullIfEmpty(settings.Title),
                    UpdatedAt: NowMs,
                    Stale: false,
                    Error: errorMsg,
                    Photos: feedItems,
                    CloudOnlyCount: cloudOnlyCount);

                _manifests[key] = new CachedManifest(response, response.UpdatedAt);
                return response;
            }
        }
        catch (BroadFolderException ex)
        {
            return new PhotoFeedResponse(
                Configured: true,
                Provider: "local",
                SourceLabel: "Computer folder",
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: sanitizeErrors ? "Choose a specific subfolder in PhoneBox Settings" : ex.Message,
                Photos: []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (staleFallback is not null && staleFallback.Response.Photos.Count > 0)
            {
                return staleFallback.Response with
                {
                    Stale = true,
                    Error = "Photo folder is unavailable",
                };
            }
            return new PhotoFeedResponse(
                Configured: true,
                Provider: "local",
                SourceLabel: "Computer folder",
                Title: NullIfEmpty(settings.Title),
                UpdatedAt: NowMs,
                Stale: false,
                Error: sanitizeErrors ? "Photo folder is unavailable" : "Selected folder was not found or could not be opened",
                Photos: []);
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(key, out var cur) && ReferenceEquals(cur.CancelCts, cancelCts))
                    _inFlight.Remove(key);
            }
            cancelCts.Dispose();
        }
    }

    private static PhotoFeedResponse ApplyPresentationOrder(PhotoFeedResponse resp, PhotoWidgetSettings settings)
    {
        if (resp.Photos.Count <= 1 && string.IsNullOrEmpty(settings.Title))
            return resp;

        IEnumerable<PhotoFeedItem> ordered = settings.Order switch
        {
            "newest" => resp.Photos.OrderByDescending(p => p.TakenAt ?? 0L).ThenBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            "oldest" => resp.Photos.OrderBy(p => p.TakenAt ?? long.MaxValue).ThenBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            "name" => resp.Photos.OrderBy(p => p.Caption ?? "", StringComparer.OrdinalIgnoreCase),
            _ => resp.Photos,
        };
        return resp with
        {
            Title = NullIfEmpty(settings.Title),
            Photos = ordered.ToList(),
        };
    }


    private void RegisterItemLocked(ScannedPhoto sItem, string sourceKey, bool forPreview)
    {
        if (!_items.TryGetValue(sItem.Id, out var reg))
        {
            while ((_items.Count >= MaxItemRegistry || _itemOrder.Count >= MaxItemRegistry * 2) && _itemOrder.Count > 0)
            {
                var victim = _itemOrder.Dequeue();
                _items.Remove(victim);
            }
            reg = new RegisteredPhoto
            {
                Id = sItem.Id,
                LocalFilePath = sItem.LocalFilePath,
                LocalRootPath = sItem.LocalRootPath,
                TakenAtMs = sItem.TakenAtMs,
                Caption = sItem.Caption,
                Album = sItem.Album,
                SourceLabel = sItem.SourceLabel,
                AllowCloudDownload = sItem.AllowCloudDownload,
                IsCloudOnly = sItem.IsCloudOnly,
                AllowPreview = forPreview,
            };
            _itemOrder.Enqueue(sItem.Id);
            _items[sItem.Id] = reg;
        }
        else if (forPreview)
        {
            reg.AllowPreview = true;
        }

        if (!forPreview)
            reg.SourceKeys.Add(sourceKey);
    }

    public void Dispose()
    {
        _materializationGate.Dispose();
    }
}
