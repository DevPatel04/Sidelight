using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Manages local declarative and static HTML plugins stored in <see cref="AppPaths.PluginsDir"/>.
/// Local-only mode: no outbound HTTP, no marketplace polling, no exec/sidecar processes, and no bridge tokens.
/// </summary>
public sealed partial class PluginService
{
    public const string SecretPlaceholder = "__UNCHANGED__";
    public const long MaxZipBytes = 8 * 1024 * 1024;
    public const long MaxExtractedBytes = 8 * 1024 * 1024;
    public const int MaxZipEntries = 128;

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly JsonSerializerOptions HtmlSafeJsonOpts = new() { Encoder = JavaScriptEncoder.Default, WriteIndented = false };

    private readonly ConfigStore _cfg;
    private readonly PushStore _push;
    private readonly ILogger<PluginService> _log;
    private readonly string _pluginsDir;
    private readonly string _statePath;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private PluginsStateFile _state = new();
    private Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registeredTypes = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, (int MinW, int MinH)> GlobalWidgetMinSizes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, HashSet<string>> GlobalPublicOptionKeys = new(StringComparer.Ordinal);

    public PluginService(ConfigStore cfg, PushStore push, ILogger<PluginService> log, string? pluginsDir = null, string? statePath = null)
    {
        _cfg = cfg;
        _push = push;
        _log = log;
        _pluginsDir = pluginsDir ?? AppPaths.PluginsDir;
        _statePath = statePath ?? AppPaths.PluginsStatePath;
        LoadState();
        ReloadPluginsFromDisk();
    }

    internal string PluginsDir => _pluginsDir;

    public static (int MinW, int MinH) GetRegisteredMinSize(string widgetType) =>
        GlobalWidgetMinSizes.TryGetValue(widgetType, out var size) ? size : (2, 2);

    public static Dictionary<string, JsonElement>? FilterPublicWidgetOptions(string widgetType, Dictionary<string, JsonElement>? options)
    {
        if (options is null || options.Count == 0) return null;
        if (!GlobalPublicOptionKeys.TryGetValue(widgetType, out var allowed)) return [];
        return options.Where(kv => allowed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static object PublicOptionSchema(PluginOptionDef o) => new
    {
        key = o.Key, label = o.Label, type = o.Type, secret = o.IsSecret,
        @default = o.IsSecret ? null : o.Default, min = o.Min, max = o.Max, choices = o.Choices,
    };

    public string ActiveCatalogWidgetsJson()
    {
        List<object> entries = [];
        lock (_lock)
        {
            foreach (var p in _loaded.Values.Where(x => x.Active).OrderBy(x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var w in p.Manifest.Widgets)
                entries.Add(new
                {
                    type = w.Type, category = w.Category, name = w.Name, icon = w.Icon, desc = w.Desc,
                    w = w.W, h = w.H, minW = w.MinW, minH = w.MinH, plugin = p.Manifest.Id, uiKind = w.Ui.Kind,
                    options = w.Options.Select(PublicOptionSchema).ToList(),
                });
        }
        return entries.Count == 0 ? "" : JsonSerializer.Serialize(entries, JsonWriteOpts)[1..^1];
    }

    public object GetOverview()
    {
        List<LoadedPlugin> snapshot;
        lock (_lock) snapshot = _loaded.Values.OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var installed = snapshot.Select(p =>
        {
            var s = p.State;
            var secretSet = p.Manifest.Settings
                .Where(o => o.IsSecret && s.Settings.TryGetValue(o.Key, out var val) && val.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(val.GetString()))
                .Select(o => o.Key).ToArray();
            var publicSettings = p.Manifest.Settings
                .Where(o => !o.IsSecret && s.Settings.ContainsKey(o.Key))
                .ToDictionary(o => o.Key, o => s.Settings[o.Key], StringComparer.Ordinal);
            return new
            {
                id = p.Manifest.Id, name = p.Manifest.Name, version = p.Manifest.Version, author = p.Manifest.Author,
                description = p.Manifest.Description, homepage = p.Manifest.Homepage, source = s.Source, sha256 = s.Sha256,
                enabled = s.Enabled, active = p.Active, needsApproval = p.NeedsApproval, needsReapproval = s.NeedsReapproval,
                changeSummary = s.ChangeSummary, error = p.LoadError, osOk = p.OsOk, agentVersionOk = p.AgentVersionOk,
                permissions = p.Manifest.Permissions, approvedPermissions = s.ApprovedPermissions,
                settingsSchema = p.Manifest.Settings.Select(PublicOptionSchema),
                settings = publicSettings, secretKeysSet = secretSet,
                widgets = p.Manifest.Widgets.Select(w => new { w.Type, w.Name, w.Icon, w.Category, ui = w.Ui.Kind }),
            };
        }).ToList();

        return new { pluginsDir = _pluginsDir, installed };
    }

    public async Task<string?> InstallFromPathAsync(string? rawPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "plugin path is required";
        var full = Path.GetFullPath(rawPath.Trim());
        if (File.Exists(full))
        {
            if (PluginManifestValidator.IsSymlinkOrReparsePoint(new FileInfo(full))) return "symbolic links are not permitted";
            if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return "local file must be a .zip plugin archive or a plugin folder";
            if (new FileInfo(full).Length > MaxZipBytes) return $"plugin archive exceeds the {MaxZipBytes / (1024 * 1024)} MB limit";
            await using var fs = File.OpenRead(full);
            return await InstallFromZipAsync(fs, ct);
        }
        if (!Directory.Exists(full)) return "plugin zip or folder not found";
        if (PluginManifestValidator.IsSymlinkOrReparsePoint(new DirectoryInfo(full))) return "symbolic links are not permitted";
        return await RunStagedInstallAsync(workDir => Task.FromResult(CopyDirectorySafely(full, workDir)), ct);
    }

    public Task<string?> InstallFromZipAsync(Stream zipStream, CancellationToken ct = default) =>
        RunStagedInstallAsync(async workDir =>
        {
            var zipPath = workDir + ".zip";
            try
            {
                await using (var dst = AppPaths.CreatePrivate(zipPath))
                {
                    var buf = new byte[64 * 1024];
                    long total = 0;
                    int read;
                    while ((read = await zipStream.ReadAsync(buf, ct)) > 0)
                    {
                        if ((total += read) > MaxZipBytes) return $"plugin archive exceeds the {MaxZipBytes / (1024 * 1024)} MB limit";
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    }
                }
                return ExtractZipSafely(zipPath, workDir);
            }
            catch (InvalidDataException) { return "invalid plugin .zip archive"; }
            finally { try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { } }
        }, ct);

    private async Task<string?> RunStagedInstallAsync(Func<string, Task<string?>> populateWorkDir, CancellationToken ct)
    {
        await _installGate.WaitAsync(ct);
        try
        {
            AppPaths.CreatePrivateDirectory(_pluginsDir);
            var stagingRoot = Path.Combine(_pluginsDir, ".staging");
            AppPaths.CreatePrivateDirectory(stagingRoot);
            var workDir = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            AppPaths.CreatePrivateDirectory(workDir);
            try { return await populateWorkDir(workDir) ?? await FinishInstallFromWorkDirAsync(workDir, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
            finally { TryDeleteDirectory(workDir); }
        }
        finally { _installGate.Release(); }
    }

    private async Task<string?> FinishInstallFromWorkDirAsync(string workDir, CancellationToken ct)
    {
        var sourceDir = UnwrapSingleRoot(workDir);
        var manifestPath = Path.Combine(sourceDir, "plugin.json");
        if (!File.Exists(manifestPath)) return "plugin does not contain plugin.json";
        if (PluginManifestValidator.IsSymlinkOrReparsePoint(new FileInfo(manifestPath))) return "symbolic links are not permitted";

        PluginManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath, ct), JsonReadOpts); }
        catch (JsonException ex) { return "invalid plugin.json: " + ex.Message; }
        if (PluginManifestValidator.Validate(manifest, sourceDir) is { } valErr) return valErr;

        var id = manifest!.Id;
        var contentSha = ComputeDirectorySha256(sourceDir);
        var stagingRoot = Path.Combine(_pluginsDir, ".staging");
        var targetDir = Path.Combine(_pluginsDir, id);
        var stagedTarget = Path.Combine(stagingRoot, $"{id}.new-{Guid.NewGuid():N}");
        var backupTarget = Path.Combine(stagingRoot, $"{id}.old-{Guid.NewGuid():N}");

        try
        {
            if (CopyDirectorySafely(sourceDir, stagedTarget) is { } stageErr) return stageErr;
            if (Directory.Exists(targetDir)) Directory.Move(targetDir, backupTarget);
            Directory.Move(stagedTarget, targetDir);
            TryDeleteDirectory(backupTarget);
        }
        catch
        {
            TryDeleteDirectory(stagedTarget);
            if (!Directory.Exists(targetDir) && Directory.Exists(backupTarget)) try { Directory.Move(backupTarget, targetDir); } catch { }
            throw;
        }

        string stateJson;
        lock (_lock)
        {
            if (_state.Plugins.TryGetValue(id, out var prev))
                ApplyManifestChange(prev, manifest, contentSha);
            else
                _state.Plugins[id] = new StoredPluginState { Enabled = false, Version = manifest.Version, Source = "local", Sha256 = contentSha };
            stateJson = SnapshotStateJsonLocked();
        }
        WriteStateFile(stateJson);
        ReloadPluginsFromDisk();
        _push.Emit("refresh");
        _log.LogInformation("installed local plugin {Id} v{Version} (enabled=false)", id, manifest.Version);
        return null;
    }

    private async Task<string?> CommitStateAsync(string id, Func<LoadedPlugin, string?> mutateLocked)
    {
        if (!PluginManifestValidator.IsValidId(id)) return "invalid plugin id";
        await _installGate.WaitAsync();
        try
        {
            string stateJson;
            lock (_lock)
            {
                if (!_loaded.TryGetValue(id, out var plugin)) return $"plugin '{id}' is not installed";
                if (mutateLocked(plugin) is { } err) return err;
                _state.Plugins[id] = plugin.State;
                stateJson = SnapshotStateJsonLocked();
            }
            WriteStateFile(stateJson);
            ReloadPluginsFromDisk();
            _push.Emit("refresh");
            return null;
        }
        finally { _installGate.Release(); }
    }

    public Task<string?> SetEnabledAsync(string id, bool enabled, IReadOnlyList<string>? approvedPermissions = null, bool approveChanges = false) =>
        CommitStateAsync(id, plugin =>
        {
            if (!string.IsNullOrEmpty(plugin.LoadError)) return plugin.LoadError;
            if (enabled)
            {
                if (!plugin.OsOk) return $"plugin '{id}' does not support {AppPaths.Os}";
                if (!plugin.AgentVersionOk) return $"plugin '{id}' requires PhoneBox Agent {plugin.Manifest.MinAgentVersion}";
                if (plugin.State.NeedsReapproval && !approveChanges) return "plugin update changes must be approved before enabling";
                var approved = new HashSet<string>(plugin.State.ApprovedPermissions ?? [], StringComparer.Ordinal);
                if (approvedPermissions is not null) foreach (var p in approvedPermissions) approved.Add(p);
                var missing = plugin.Manifest.Permissions.Where(p => !approved.Contains(p)).ToList();
                if (missing.Count > 0) return "permissions must be approved before enabling: " + string.Join(", ", missing);
                plugin.State.ApprovedPermissions = [.. approved];
                plugin.State.NeedsReapproval = false;
                plugin.State.ChangeSummary = null;
            }
            plugin.State.Enabled = enabled;
            plugin.State.Version = plugin.Manifest.Version;
            plugin.State.Sha256 ??= plugin.DiskSha256;
            return null;
        });

    public Task<string?> SaveSettingsAsync(string id, Dictionary<string, JsonElement>? incoming) =>
        CommitStateAsync(id, plugin =>
        {
            var updated = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var opt in plugin.Manifest.Settings)
            {
                JsonElement newVal = default;
                var hasIncoming = incoming is not null && incoming.TryGetValue(opt.Key, out newVal);
                if (opt.IsSecret)
                {
                    if (!hasIncoming || (newVal.ValueKind == JsonValueKind.String && newVal.GetString() == SecretPlaceholder))
                    { if (plugin.State.Settings.TryGetValue(opt.Key, out var prevVal)) updated[opt.Key] = prevVal.Clone(); }
                    else if (newVal.ValueKind != JsonValueKind.Null && !(newVal.ValueKind == JsonValueKind.String && newVal.GetString() == ""))
                        updated[opt.Key] = newVal.Clone();
                }
                else if (hasIncoming && newVal.ValueKind != JsonValueKind.Undefined) updated[opt.Key] = newVal.Clone();
                else if (opt.Default is { } def) updated[opt.Key] = def.Clone();
            }
            plugin.State.Settings = updated;
            return null;
        });

    public async Task<string?> UninstallAsync(string id)
    {
        if (!PluginManifestValidator.IsValidId(id)) return "invalid plugin id";
        await _installGate.WaitAsync();
        try
        {
            TryDeleteDirectory(Path.Combine(_pluginsDir, id));
            string stateJson;
            lock (_lock) { _state.Plugins.Remove(id); stateJson = SnapshotStateJsonLocked(); }
            WriteStateFile(stateJson);
            ReloadPluginsFromDisk();
            _push.Emit("refresh");
            return null;
        }
        finally { _installGate.Release(); }
    }

    public object GetWidgetState(string pluginId, string pageId, string widgetId, string? deviceId = null)
    {
        LoadedPlugin? plugin;
        lock (_lock) _loaded.TryGetValue(pluginId, out plugin);
        if (plugin is null) return new { ok = false, status = "missing", error = $"Plugin '{pluginId}' is not installed" };
        if (!plugin.Active) return new { ok = false, status = "disabled", error = plugin.LoadError ?? $"Plugin '{plugin.Manifest.Name}' is disabled" };

        var resolved = ResolveConfiguredWidget(pageId, widgetId, plugin, deviceId);
        if (resolved is null) return new { ok = false, status = "not_found", error = "Widget not found in settings" };

        var (widgetCfg, widgetDef) = resolved.Value;
        var publicOpts = CollectPublicValues(widgetDef.Options, FilterPublicWidgetOptions(widgetDef.Type, widgetCfg.Options));
        var publicSettings = CollectPublicValues(plugin.Manifest.Settings, plugin.State.Settings);

        var html = widgetDef.Ui.Kind == "html" && plugin.HtmlFiles.TryGetValue(widgetDef.Type, out var rawHtml)
            ? InterpolateHtmlTemplate(rawHtml, publicSettings, publicOpts) : null;

        JsonElement dataEl;
        if (widgetDef.Ui.Data is { } templateData && templateData.ValueKind == JsonValueKind.Object)
            dataEl = InterpolateDeclarativeElement(templateData, publicSettings, publicOpts);
        else
            dataEl = JsonSerializer.SerializeToElement(new
            {
                title = publicOpts.TryGetValue("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : widgetDef.Name,
                value = widgetDef.Ui.Kind == "html" ? null : widgetDef.Name,
                sub = string.IsNullOrWhiteSpace(plugin.Manifest.Description) ? $"v{plugin.Manifest.Version}" : plugin.Manifest.Description,
                options = publicOpts, settings = publicSettings,
            });

        return new
        {
            ok = true, status = "ready",
            ui = new { kind = widgetDef.Ui.Kind, name = widgetDef.Name, icon = widgetDef.Icon, html },
            controlAllowed = widgetCfg.Bool("control") != false, data = dataEl, error = (string?)null,
        };
    }

    internal static string InterpolateHtmlTemplate(string template, IReadOnlyDictionary<string, JsonElement> settings, IReadOnlyDictionary<string, JsonElement> options) =>
        string.IsNullOrEmpty(template) ? "" : TemplateTokenRegex().Replace(template, m =>
        {
            var src = m.Groups[1].Value == "settings" ? settings : options;
            if (!m.Groups[2].Success) return JsonSerializer.Serialize(src, HtmlSafeJsonOpts);
            return src.TryGetValue(m.Groups[2].Value, out var val) ? JsonSerializer.Serialize(val, HtmlSafeJsonOpts) : "null";
        });

    private static JsonElement InterpolateDeclarativeElement(JsonElement el, IReadOnlyDictionary<string, JsonElement> settings, IReadOnlyDictionary<string, JsonElement> options)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                return JsonSerializer.SerializeToElement(el.EnumerateObject().ToDictionary(p => p.Name, p => InterpolateDeclarativeElement(p.Value, settings, options), StringComparer.Ordinal), HtmlSafeJsonOpts);
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(el.EnumerateArray().Select(item => InterpolateDeclarativeElement(item, settings, options)).ToList(), HtmlSafeJsonOpts);
            case JsonValueKind.String:
            {
                var s = el.GetString() ?? "";
                var exact = TemplateTokenRegex().Match(s);
                if (exact.Success && exact.Index == 0 && exact.Length == s.Length)
                {
                    var src = exact.Groups[1].Value == "settings" ? settings : options;
                    if (!exact.Groups[2].Success) return JsonSerializer.SerializeToElement(src, HtmlSafeJsonOpts);
                    return src.TryGetValue(exact.Groups[2].Value, out var val) ? val.Clone() : JsonSerializer.SerializeToElement("");
                }
                var replaced = TemplateTokenRegex().Replace(s, m =>
                {
                    var src = m.Groups[1].Value == "settings" ? settings : options;
                    if (!m.Groups[2].Success) return JsonSerializer.Serialize(src, HtmlSafeJsonOpts);
                    return src.TryGetValue(m.Groups[2].Value, out var val) ? ElementToText(val) : "";
                });
                return JsonSerializer.SerializeToElement(replaced, HtmlSafeJsonOpts);
            }
            default: return el.Clone();
        }
    }

    private static Dictionary<string, JsonElement> CollectPublicValues(IEnumerable<PluginOptionDef> defs, Dictionary<string, JsonElement>? source)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var opt in defs)
        {
            if (opt.IsSecret) continue;
            if (source is not null && source.TryGetValue(opt.Key, out var val)) result[opt.Key] = val.Clone();
            else if (opt.Default is { } def) result[opt.Key] = def.Clone();
        }
        return result;
    }

    private (WidgetConfig Widget, PluginWidgetDef Def)? ResolveConfiguredWidget(string pageId, string widgetId, LoadedPlugin plugin, string? deviceId)
    {
        var page = _cfg.Get().PagesFor(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId).FirstOrDefault(p => p.Id == pageId && p.Enabled);
        if (page?.Widgets is null) return null;
        if (page.Widgets.FirstOrDefault(w => w.Id == widgetId) is { } direct)
            return plugin.Manifest.Widgets.FirstOrDefault(x => x.Type == direct.Type) is { } def ? (direct, def) : null;
        var colon = widgetId.IndexOf(':');
        if (colon <= 0) return null;
        var stackWidget = page.Widgets.FirstOrDefault(w => w.Id == widgetId[..colon] && w.Type == "stack");
        var child = stackWidget is null ? null : StackWidgets.Children(stackWidget).FirstOrDefault(c => c.Id == widgetId[(colon + 1)..] && c.Enabled);
        return child is not null && plugin.Manifest.Widgets.FirstOrDefault(x => x.Type == child.Type) is { } cDef
            ? (new WidgetConfig { Id = widgetId, Type = child.Type, Options = child.Options }, cDef) : null;
    }

    internal void ReloadPluginsFromDisk()
    {
        var scanned = new List<DiskPluginScan>();
        if (Directory.Exists(_pluginsDir) && !PluginManifestValidator.IsSymlinkOrReparsePoint(new DirectoryInfo(_pluginsDir)))
        {
            foreach (var dir in Directory.EnumerateDirectories(_pluginsDir))
            {
                var folderName = Path.GetFileName(dir);
                if (folderName.StartsWith('.') || PluginManifestValidator.IsSymlinkOrReparsePoint(new DirectoryInfo(dir))) continue;
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    if (PluginManifestValidator.IsSymlinkOrReparsePoint(new FileInfo(manifestPath)))
                    { scanned.Add(new(folderName, dir, null, "symbolic links are not permitted", new Dictionary<string, string>(), "")); continue; }
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonReadOpts);
                    var err = PluginManifestValidator.Validate(manifest, dir);
                    if (err is not null || manifest is null)
                    { scanned.Add(new(PluginManifestValidator.IsValidId(manifest?.Id) ? manifest!.Id : folderName, dir, manifest, err ?? "invalid plugin.json", new Dictionary<string, string>(), "")); continue; }
                    var htmlFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var w in manifest.Widgets)
                        if (w.Ui.Kind == "html" && PluginManifestValidator.ResolveSafeChildPath(dir, w.Ui.File) is { } hp && File.Exists(hp))
                            htmlFiles[w.Type] = File.ReadAllText(hp, Encoding.UTF8);
                    scanned.Add(new(manifest.Id, dir, manifest, null, htmlFiles, ComputeDirectorySha256(dir)));
                }
                catch (Exception ex) when (PluginManifestValidator.IsValidId(folderName)) { scanned.Add(new(folderName, dir, null, ex.Message, new Dictionary<string, string>(), "")); }
                catch { }
            }
        }

        string? dirtyStateJson = null;
        lock (_lock)
        {
            var next = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in scanned)
            {
                if (s.Error is not null || s.Manifest is null)
                {
                    var stErr = _state.Plugins.TryGetValue(s.Id, out var prevErr) ? prevErr : new StoredPluginState();
                    next[s.Id] = new(s.Manifest ?? new PluginManifest { Id = s.Id, Name = s.Id }, s.Dir, stErr, s.HtmlFiles, "", false, false, false, false, s.Error ?? "invalid plugin.json");
                    continue;
                }
                StoredPluginState st;
                if (_state.Plugins.TryGetValue(s.Manifest.Id, out var found))
                {
                    st = found;
                    if (!string.IsNullOrEmpty(st.Sha256) && ApplyManifestChange(st, s.Manifest, s.DiskSha256))
                        dirtyStateJson = SnapshotStateJsonLocked();
                    else if (string.IsNullOrEmpty(st.Sha256))
                        st.Sha256 = s.DiskSha256;
                }
                else st = new StoredPluginState { Enabled = false, Version = s.Manifest.Version, Source = "local", Sha256 = s.DiskSha256 };

                var osOk = s.Manifest.Os.Count == 0 || s.Manifest.Os.Contains(AppPaths.Os, StringComparer.OrdinalIgnoreCase);
                var versionOk = PluginManifestValidator.IsCompatibleAgentVersion(s.Manifest.MinAgentVersion);
                var approvedSet = new HashSet<string>(st.ApprovedPermissions ?? [], StringComparer.Ordinal);
                var needsApproval = s.Manifest.Permissions.Any(p => !approvedSet.Contains(p));
                var active = st.Enabled && osOk && versionOk && !needsApproval && !st.NeedsReapproval;
                next[s.Manifest.Id] = new(s.Manifest, s.Dir, st, s.HtmlFiles, s.DiskSha256, osOk, versionOk, needsApproval, active, null);
            }

            _loaded = next;
            foreach (var oldType in _registeredTypes) { GlobalWidgetMinSizes.TryRemove(oldType, out _); GlobalPublicOptionKeys.TryRemove(oldType, out _); }
            _registeredTypes.Clear();
            foreach (var p in next.Values.Where(x => x.Active))
            foreach (var w in p.Manifest.Widgets)
            {
                _registeredTypes.Add(w.Type);
                GlobalWidgetMinSizes[w.Type] = (w.MinW, w.MinH);
                GlobalPublicOptionKeys[w.Type] = w.Options.Where(o => !o.IsSecret).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
            }
        }
        if (dirtyStateJson is not null) WriteStateFile(dirtyStateJson);
    }

    private static bool ApplyManifestChange(StoredPluginState st, PluginManifest next, string nextSha)
    {
        var changes = new List<string>();
        if (!string.Equals(st.Version, next.Version, StringComparison.OrdinalIgnoreCase))
            changes.Add($"version changed from {st.Version} to {next.Version}");
        if (!string.IsNullOrEmpty(st.Sha256) && !string.Equals(st.Sha256, nextSha, StringComparison.OrdinalIgnoreCase))
            changes.Add($"content SHA-256 changed ({ShortSha(st.Sha256)} -> {ShortSha(nextSha)})");
        if (!new HashSet<string>(st.ApprovedPermissions ?? [], StringComparer.Ordinal).SetEquals(next.Permissions ?? []))
            changes.Add("permissions changed");
        if (changes.Count == 0) return false;

        st.Enabled = false;
        st.NeedsReapproval = true;
        st.ChangeSummary = string.Join("; ", changes);
        st.Version = next.Version;
        st.Sha256 = nextSha;
        st.ApprovedPermissions = [];
        return true;
    }

    private static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private void LoadState()
    {
        PluginsStateFile loaded = new();
        if (File.Exists(_statePath))
        {
            AppPaths.RestrictToOwner(_statePath);
            try { loaded = JsonSerializer.Deserialize<PluginsStateFile>(File.ReadAllText(_statePath), JsonReadOpts) ?? new(); }
            catch { loaded = new(); }
        }
        loaded.Plugins ??= new(StringComparer.OrdinalIgnoreCase);
        lock (_lock) _state = loaded;
    }

    private string SnapshotStateJsonLocked() => JsonSerializer.Serialize(_state, JsonWriteOpts);

    private void WriteStateFile(string json)
    {
        AppPaths.CreatePrivateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        AppPaths.WriteAllTextPrivate(tmp, json);
        File.Move(tmp, _statePath, overwrite: true);
    }

    private static string? ExtractZipSafely(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destWithSep = destFull + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seenFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxZipEntries) return $"plugin archive exceeds the {MaxZipEntries} entry limit";

        long totalUncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) return "symbolic links are not permitted inside plugin archives";
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) return "reparse points are not permitted inside plugin archives";
            if (entry.FullName.IndexOf('\0') >= 0) return "unsafe path inside plugin archive";
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized)) continue;
            if (normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(entry.FullName)) return "unsafe path inside plugin archive";
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or "..")) return "unsafe path inside plugin archive";
            var target = Path.GetFullPath(Path.Combine(destFull, normalized));
            if (!target.StartsWith(destWithSep, cmp)) return "unsafe path inside plugin archive";
            if (normalized.EndsWith('/')) { AppPaths.CreatePrivateDirectory(target); continue; }
            if (!seenFiles.Add(target)) return "duplicate entry inside plugin archive";
            if (entry.Length > MaxExtractedBytes) return $"extracted plugin exceeds the {MaxExtractedBytes / (1024 * 1024)} MB limit";

            AppPaths.CreatePrivateDirectory(Path.GetDirectoryName(target)!);
            using var src = entry.Open();
            using var dst = AppPaths.CreatePrivate(target);
            var buf = new byte[64 * 1024];
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                if ((totalUncompressed += read) > MaxExtractedBytes) return $"extracted plugin exceeds the {MaxExtractedBytes / (1024 * 1024)} MB limit";
                dst.Write(buf, 0, read);
            }
        }
        return null;
    }

    private static string? CopyDirectorySafely(string source, string destination)
    {
        AppPaths.CreatePrivateDirectory(destination);
        var entries = 0;
        long totalBytes = 0;
        var queue = new Queue<(string Src, string Dst)>();
        queue.Enqueue((source, destination));
        while (queue.Count > 0)
        {
            var (curSrc, curDst) = queue.Dequeue();
            foreach (var dir in Directory.EnumerateDirectories(curSrc))
            {
                if (++entries > MaxZipEntries) return $"plugin folder exceeds the {MaxZipEntries} entry limit";
                var info = new DirectoryInfo(dir);
                if (PluginManifestValidator.IsSymlinkOrReparsePoint(info)) return "symbolic links are not permitted inside plugin folders";
                var dstSub = Path.Combine(curDst, info.Name);
                AppPaths.CreatePrivateDirectory(dstSub);
                queue.Enqueue((dir, dstSub));
            }
            foreach (var file in Directory.EnumerateFiles(curSrc))
            {
                if (++entries > MaxZipEntries) return $"plugin folder exceeds the {MaxZipEntries} entry limit";
                var info = new FileInfo(file);
                if (PluginManifestValidator.IsSymlinkOrReparsePoint(info)) return "symbolic links are not permitted inside plugin folders";
                if ((totalBytes += info.Length) > MaxExtractedBytes) return $"plugin folder exceeds the {MaxExtractedBytes / (1024 * 1024)} MB limit";
                AppPaths.CopyPrivate(file, Path.Combine(curDst, info.Name));
            }
        }
        return null;
    }

    private static string UnwrapSingleRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "plugin.json"))) return dir;
        var subDirs = Directory.GetDirectories(dir);
        return Directory.GetFiles(dir).Length == 0 && subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "plugin.json")) ? subDirs[0] : dir;
    }

    internal static string ComputeDirectorySha256(string dir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(dir, f).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.Ordinal);
        Span<byte> lenBuf = stackalloc byte[8];
        var buf = new byte[64 * 1024];
        foreach (var (full, rel) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(rel));
            hash.AppendData("\0"u8);
            BitConverter.TryWriteBytes(lenBuf, new FileInfo(full).Length);
            hash.AppendData(lenBuf);
            using var fs = File.OpenRead(full);
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0) hash.AppendData(buf.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ElementToText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    [GeneratedRegex(@"\{(settings|options)(?:\.([A-Za-z0-9_.-]+))?\}")]
    private static partial Regex TemplateTokenRegex();

    private sealed record DiskPluginScan(string Id, string Dir, PluginManifest? Manifest, string? Error, IReadOnlyDictionary<string, string> HtmlFiles, string DiskSha256);
    internal sealed record LoadedPlugin(PluginManifest Manifest, string Dir, StoredPluginState State, IReadOnlyDictionary<string, string> HtmlFiles, string DiskSha256, bool OsOk, bool AgentVersionOk, bool NeedsApproval, bool Active, string? LoadError);
}
