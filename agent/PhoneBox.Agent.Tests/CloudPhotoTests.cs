using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhoneBox.Agent;
using Xunit;

public sealed class CloudPhotoTests
{
    private sealed class TempPhotoEnv : IDisposable
    {
        public string RootDir { get; } = Path.Combine(Path.GetTempPath(), "phonebox-photos-test-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(RootDir, "config.json");
        public string CacheDir => Path.Combine(RootDir, "photos-cache");
        public string PhotoDir => Path.Combine(RootDir, "photos");

        public TempPhotoEnv()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(PhotoDir);
        }

        public ConfigStore WriteConfig(object config)
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ConfigStore.WriteOpts));
            return new ConfigStore(ConfigPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); } catch { }
        }
    }

    private sealed class RealHttpFixture : IAsyncDisposable
    {
        public WebApplication App { get; }
        public int LocalPort { get; private set; }
        public int LanPort { get; private set; }
        public ConfigStore Config { get; }
        public CloudPhotoService Photos { get; }
        public HttpClient Http { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false });

        private RealHttpFixture(WebApplication app, ConfigStore config, CloudPhotoService photos)
        {
            App = app;
            Config = config;
            Photos = photos;
        }

        public static async Task<RealHttpFixture> StartAsync(
            ConfigStore config,
            string cacheDir,
            Func<long>? nowMsProvider = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, 0);
                k.Listen(IPAddress.Loopback, 0);
            });
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
            var photos = new CloudPhotoService(config, cacheDir, nowMsProvider: nowMsProvider);
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton(photos);

            var app = builder.Build();
            var fixture = new RealHttpFixture(app, config, photos);
            PhotoEndpoints.UseSecurityGate(app, config, () => fixture.LocalPort, () => fixture.LanPort);
            app.MapGet("/api/config", (ConfigStore cfg, HttpRequest req) =>
                Results.Json(new { version = cfg.Version, config = cfg.Get().ToPublic(req.Headers["X-PhoneBox-Device"].ToString()) }));
            PhotoEndpoints.Map(app, () => fixture.LanPort);

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
            fixture.LocalPort = new Uri(addresses[0]).Port;
            fixture.LanPort = new Uri(addresses[1]).Port;
            return fixture;
        }

        public Uri LocalUri(string pathAndQuery) => new($"http://127.0.0.1:{LocalPort}{pathAndQuery}");
        public Uri LanUri(string pathAndQuery) => new($"http://127.0.0.1:{LanPort}{pathAndQuery}");

        public HttpRequestMessage LanGet(string pathAndQuery, string? deviceId = null, string? token = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, LanUri(pathAndQuery));
            var lanToken = token ?? Config.Get().Lan.Token!;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lanToken);
            if (deviceId is not null) req.Headers.TryAddWithoutValidation("X-PhoneBox-Device", deviceId);
            return req;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync();
            Photos.Dispose();
        }
    }

    private static readonly byte[] MinimalPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static readonly byte[] MinimalJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    // ---------------------------------------------------------------- Real HTTP middleware tests

    [Fact]
    public async Task LanMiddlewareEnforcesDeviceHeaderPagesForScopingAndDisabledState()
    {
        using var env = new TempPhotoEnv();
        var dirA = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "album-a")).FullName;
        var dirB = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "album-b")).FullName;
        var dirDisabled = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "album-disabled")).FullName;
        File.WriteAllBytes(Path.Combine(dirA, "a.png"), MinimalPng);
        File.WriteAllBytes(Path.Combine(dirB, "b.jpg"), MinimalJpeg);
        File.WriteAllBytes(Path.Combine(dirDisabled, "d.png"), MinimalPng);

        var store = env.WriteConfig(new
        {
            lan = new { enabled = true, token = "lan-secret-1" },
            phones = new object[]
            {
                new { id = "phone-a", name = "Phone A", pages = new[] { "page-a" } },
                new { id = "phone-b", name = "Phone B", pages = new[] { "page-b" } },
                new { id = "phone-dis", name = "Phone Disabled", pages = new[] { "page-disabled" } },
            },
            pages = new object[]
            {
                new
                {
                    id = "page-a", title = "A", enabled = true,
                    widgets = new object[]
                    {
                        new { id = "ph-a", type = "photos", x = 0, y = 0, w = 6, h = 4, options = new { provider = "local", folder = dirA } }
                    }
                },
                new
                {
                    id = "page-b", title = "B", enabled = true,
                    widgets = new object[]
                    {
                        new
                        {
                            id = "st-b", type = "stack", x = 0, y = 0, w = 6, h = 4,
                            options = new
                            {
                                widgets = new object[]
                                {
                                    new { id = "ph-b", type = "photos", enabled = true, options = new { provider = "local", folder = dirB } },
                                    new { id = "ph-b-off", type = "photos", enabled = false, options = new { provider = "local", folder = dirDisabled } },
                                }
                            }
                        }
                    }
                },
                new
                {
                    id = "page-disabled", title = "Disabled", enabled = false,
                    widgets = new object[]
                    {
                        new { id = "ph-dis", type = "photos", x = 0, y = 0, w = 6, h = 4, options = new { provider = "local", folder = dirDisabled } }
                    }
                }
            }
        });

        await using var srv = await RealHttpFixture.StartAsync(store, env.CacheDir);

        // 1. Missing bearer token on LAN -> 403
        using (var noAuth = await srv.Http.GetAsync(srv.LanUri("/api/photos/feed?widgetId=ph-a")))
            Assert.Equal(HttpStatusCode.Forbidden, noAuth.StatusCode);

        // 2. Missing X-PhoneBox-Device on LAN -> 403 for both feed and image
        using (var req = srv.LanGet("/api/photos/feed?widgetId=ph-a", deviceId: null))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        using (var req = srv.LanGet("/api/photos/image/some-id", deviceId: null))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // 3. Local-only photo routes stay 403 over LAN even with Bearer token + device header
        foreach (var localOnly in new[]
        {
            (HttpMethod.Get, "/api/photos/status"),
            (HttpMethod.Get, "/api/photos/local-roots"),
            (HttpMethod.Post, "/api/photos/browse"),
            (HttpMethod.Post, "/api/photos/test"),
            (HttpMethod.Post, "/api/photos/preview-feed"),
        })
        {
            using var req = new HttpRequestMessage(localOnly.Item1, srv.LanUri(localOnly.Item2));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "lan-secret-1");
            req.Headers.TryAddWithoutValidation("X-PhoneBox-Device", "phone-a");
            using var res = await srv.Http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        // 4. Phone A fetches its own feed & image over LAN
        string idA;
        using (var req = srv.LanGet("/api/photos/feed?widgetId=ph-a", "phone-a"))
        using (var res = await srv.Http.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var photosArr = doc.RootElement.GetProperty("photos");
            Assert.Single(photosArr.EnumerateArray());
            idA = photosArr[0].GetProperty("id").GetString()!;
        }

        using (var req = srv.LanGet($"/api/photos/image/{idA}", "phone-a"))
        using (var res = await srv.Http.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal(MinimalPng, await res.Content.ReadAsByteArrayAsync());
        }

        // 5. Phone B fetches Smart Stack child ph-b, cannot fetch disabled child ph-b-off or Phone A's ph-a/image
        string idB;
        using (var req = srv.LanGet("/api/photos/feed?widgetId=st-b:ph-b", "phone-b"))
        using (var res = await srv.Http.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            idB = doc.RootElement.GetProperty("photos")[0].GetProperty("id").GetString()!;
        }

        using (var req = srv.LanGet("/api/photos/feed?widgetId=st-b:ph-b-off", "phone-b"))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        using (var req = srv.LanGet("/api/photos/feed?widgetId=ph-a", "phone-b"))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        using (var req = srv.LanGet($"/api/photos/image/{idA}", "phone-b"))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        using (var req = srv.LanGet($"/api/photos/image/{idB}", "phone-a"))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // 6. Disabled page cannot serve feed or image
        using (var req = srv.LanGet("/api/photos/feed?widgetId=ph-dis", "phone-dis"))
        using (var res = await srv.Http.SendAsync(req))
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task LanRefreshRateLimitCrossOriginBlockingScrubPublicAndGenericWifiErrorsWorkOverHttp()
    {
        using var env = new TempPhotoEnv();
        var missingDir = Path.Combine(env.RootDir, "SecretUserDir", "DoesNotExist");
        var validDir = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "Trip")).FullName;
        File.WriteAllBytes(Path.Combine(validDir, "one.png"), MinimalPng);

        var store = env.WriteConfig(new
        {
            lan = new { enabled = true, token = "lan-secret-2" },
            pages = new object[]
            {
                new
                {
                    id = "p1", title = "Photos", enabled = true,
                    widgets = new object[]
                    {
                        new
                        {
                            id = "ph1", type = "photos", x = 0, y = 0, w = 6, h = 4,
                            options = new
                            {
                                provider = "local",
                                folder = validDir,
                                folderPath = validDir,
                                subfolders = false,
                                downloadCloudFiles = true,
                                allowCloudDownload = true,
                                customSecret = "should-not-leak",
                                title = "  My Trip  ",
                                interval = 1,
                                fit = "<script>",
                                transition = "explode",
                                order = "oldest",
                                info = "full",
                                clockOverlay = true,
                                tapAction = "pause",
                            }
                        },
                        new
                        {
                            id = "ph-missing", type = "photos", x = 6, y = 0, w = 6, h = 4,
                            options = new { provider = "local", folder = missingDir }
                        },
                        new
                        {
                            id = "st1", type = "stack", x = 0, y = 4, w = 6, h = 2,
                            options = new
                            {
                                widgets = new object[]
                                {
                                    new
                                    {
                                        id = "c1", type = "photos", enabled = true,
                                        options = new
                                        {
                                            provider = "local",
                                            folder = validDir,
                                            downloadCloudFiles = true,
                                            unknownKey = "secret-stack-val",
                                            title = "Stack Frame",
                                            interval = 999999,
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        long fakeClockMs = 1_700_000_000_000L;
        await using var srv = await RealHttpFixture.StartAsync(store, env.CacheDir, () => fakeClockMs);

        // 1. Cross-origin POST on local port (both Origin header and Sec-Fetch-Site) -> 403
        foreach (var path in new[] { "/api/photos/browse", "/api/photos/test", "/api/photos/preview-feed" })
        {
            using var evilOrigin = new HttpRequestMessage(HttpMethod.Post, srv.LocalUri(path))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            evilOrigin.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            using var res1 = await srv.Http.SendAsync(evilOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, res1.StatusCode);

            using var evilFetchSite = new HttpRequestMessage(HttpMethod.Post, srv.LocalUri(path))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            evilFetchSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            evilFetchSite.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            using var res2 = await srv.Http.SendAsync(evilFetchSite);
            Assert.Equal(HttpStatusCode.Forbidden, res2.StatusCode);
        }

        // 2. ScrubPublic allow-list via /api/config (top-level and Smart Stack child)
        using (var cfgRes = await srv.Http.GetAsync(srv.LocalUri("/api/config")))
        {
            Assert.Equal(HttpStatusCode.OK, cfgRes.StatusCode);
            var cfgJson = await cfgRes.Content.ReadAsStringAsync();
            Assert.DoesNotContain(validDir.Replace("\\", "\\\\"), cfgJson);
            Assert.DoesNotContain("SecretUserDir", cfgJson);
            Assert.DoesNotContain("should-not-leak", cfgJson);
            Assert.DoesNotContain("secret-stack-val", cfgJson);
            Assert.DoesNotContain("downloadCloudFiles", cfgJson);
            Assert.DoesNotContain("allowCloudDownload", cfgJson);
            Assert.DoesNotContain("folderPath", cfgJson);
            Assert.DoesNotContain("\"folder\"", cfgJson);
            Assert.DoesNotContain("\"provider\"", cfgJson);
            Assert.DoesNotContain("\"subfolders\"", cfgJson);

            using var doc = JsonDocument.Parse(cfgJson);
            var ph1Opts = doc.RootElement.GetProperty("config").GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("options");
            Assert.Equal("My Trip", ph1Opts.GetProperty("title").GetString());
            Assert.Equal(5, ph1Opts.GetProperty("interval").GetInt32());
            Assert.Equal("cover", ph1Opts.GetProperty("fit").GetString());
            Assert.Equal("fade", ph1Opts.GetProperty("transition").GetString());
            Assert.Equal("oldest", ph1Opts.GetProperty("order").GetString());
            Assert.Equal("full", ph1Opts.GetProperty("info").GetString());
            Assert.True(ph1Opts.GetProperty("clockOverlay").GetBoolean());
            Assert.Equal("pause", ph1Opts.GetProperty("tapAction").GetString());

            var childOpts = doc.RootElement.GetProperty("config").GetProperty("pages")[0].GetProperty("widgets")[2].GetProperty("options").GetProperty("widgets")[0].GetProperty("options");
            Assert.Equal("Stack Frame", childOpts.GetProperty("title").GetString());
            Assert.Equal(86400, childOpts.GetProperty("interval").GetInt32());
        }

        // 3. Rate-limit refresh=true on LAN
        using (var req1 = srv.LanGet("/api/photos/feed?widgetId=ph1&refresh=true", "phone-1"))
        using (var res1 = await srv.Http.SendAsync(req1))
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        using (var req2 = srv.LanGet("/api/photos/feed?widgetId=ph1&refresh=true", "phone-1"))
        using (var res2 = await srv.Http.SendAsync(req2))
            Assert.Equal(HttpStatusCode.TooManyRequests, res2.StatusCode);

        // Non-refresh feed still succeeds while refresh is in cooldown
        using (var reqNoRefresh = srv.LanGet("/api/photos/feed?widgetId=ph1", "phone-1"))
        using (var resNoRefresh = await srv.Http.SendAsync(reqNoRefresh))
            Assert.Equal(HttpStatusCode.OK, resNoRefresh.StatusCode);

        // Advancing past cooldown lets refresh=true succeed again
        fakeClockMs += 16_000;
        using (var req3 = srv.LanGet("/api/photos/feed?widgetId=ph1&refresh=true", "phone-1"))
        using (var res3 = await srv.Http.SendAsync(req3))
            Assert.Equal(HttpStatusCode.OK, res3.StatusCode);

        // 4. Generic English errors over Wi-Fi (no filesystem paths or ex.Message)
        using (var req = srv.LanGet("/api/photos/feed?widgetId=ph-missing", "phone-1"))
        using (var res = await srv.Http.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SecretUserDir", body);
            Assert.DoesNotContain("DoesNotExist", body);
            Assert.DoesNotContain(env.RootDir, body);
            Assert.Contains("Photo folder is unavailable", body);
        }
    }

    // ---------------------------------------------------------------- Local & synced folder safety tests

    [Fact]
    public async Task IsSourceConfiguredRequiresFolderAndRejectsOverlyBroadPaths()
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new
        {
            pages = new[]
            {
                new
                {
                    id = "main", title = "Main", enabled = true,
                    widgets = new object[]
                    {
                        new { id = "empty-ph", type = "photos", x = 0, y = 0, w = 6, h = 4, options = new { provider = "local", folder = "" } }
                    }
                }
            }
        });

        using var service = new CloudPhotoService(store, env.CacheDir);

        var emptyFeed = await service.GetWidgetFeedAsync("empty-ph", refresh: false, default);
        Assert.False(emptyFeed.Configured);
        Assert.Empty(emptyFeed.Photos);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        foreach (var broad in new[] { "/", @"C:\", "/Users", "/home", @"C:\Users", "~", home })
        {
            if (string.IsNullOrWhiteSpace(broad)) continue;
            Assert.True(CloudPhotoService.IsOverlyBroadLocalFolder(broad), $"Expected '{broad}' to be rejected as overly broad");

            var opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { provider = "local", folder = broad }));
            var testRes = await service.TestSourceAsync(new PhotoTestRequest(opts), default);
            Assert.False(testRes.Ok);
            Assert.Contains("subfolder", testRes.Message, StringComparison.OrdinalIgnoreCase);

            var previewRes = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(opts), default);
            Assert.Empty(previewRes.Photos);
            Assert.False(string.IsNullOrEmpty(previewRes.Error));
        }
    }

    [Fact]
    public async Task LocalFolderRespectsSubfoldersSkipsSymlinksAndRejectsIntermediateSymlinkAfterDiscovery()
    {
        using var env = new TempPhotoEnv();
        var subDir = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "sub")).FullName;
        var outsideDir = Directory.CreateDirectory(Path.Combine(env.RootDir, "outside")).FullName;
        var outsideFile = Path.Combine(outsideDir, "secret.png");
        File.WriteAllBytes(Path.Combine(env.PhotoDir, "top.png"), MinimalPng);
        File.WriteAllBytes(Path.Combine(env.PhotoDir, "disguised.jpg"), "<svg><script>alert(1)</script></svg>"u8.ToArray());
        File.WriteAllBytes(Path.Combine(subDir, "nested.jpg"), MinimalJpeg);
        File.WriteAllBytes(outsideFile, MinimalPng);

        var fileSymlinkCreated = false;
        try
        {
            File.CreateSymbolicLink(Path.Combine(env.PhotoDir, "escape.png"), outsideFile);
            fileSymlinkCreated = true;
        }
        catch { }

        var store = env.WriteConfig(new { });
        using var service = new CloudPhotoService(store, env.CacheDir);

        // subfolders = false excludes nested.jpg
        var flatOpts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            provider = "local",
            folder = env.PhotoDir,
            subfolders = false
        }));
        var flatFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(flatOpts), default);
        Assert.DoesNotContain(flatFeed.Photos, p => p.Caption == "nested");

        // subfolders = true includes top and nested, skips file symlink, rejects disguised SVG
        var deepOpts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            provider = "local",
            folder = env.PhotoDir,
            subfolders = true
        }));
        var feed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(deepOpts), default);
        Assert.Contains(feed.Photos, p => p.Caption == "top");
        var nestedItem = Assert.Single(feed.Photos, p => p.Caption == "nested");
        if (fileSymlinkCreated) Assert.DoesNotContain(feed.Photos, p => p.Caption == "escape");

        var disguised = Assert.Single(feed.Photos, p => p.Caption == "disguised");
        Assert.Null(await service.GetPhotoBytesAsync(disguised.Id, default));

        Assert.NotNull(await service.GetPhotoBytesAsync(nestedItem.Id, default));

        // Replace intermediate directory `sub` with a symlink after feed discovery -> must reject!
        Directory.Delete(subDir, recursive: true);
        var dirSymlinkCreated = false;
        try
        {
            Directory.CreateSymbolicLink(subDir, outsideDir);
            File.WriteAllBytes(Path.Combine(outsideDir, "nested.jpg"), MinimalJpeg);
            dirSymlinkCreated = true;
        }
        catch { }

        if (dirSymlinkCreated)
        {
            Assert.False(CloudPhotoService.HasNoIntermediateReparsePoints(env.PhotoDir, Path.Combine(subDir, "nested.jpg")));
            Assert.Null(await service.GetPhotoBytesAsync(nestedItem.Id, default));
            var browse = await service.BrowseAsync(new PhotoBrowseRequest("local", null, env.PhotoDir), default);
            Assert.DoesNotContain(browse.Entries, e => e.Name == "sub");
        }
    }

    [Fact]
    public async Task PrivateDiskCacheUses0700And0600CleansTmpOnErrorsAndPrunes()
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new { cloudPhotos = new { cacheMaxMb = 20 } });
        using var service = new CloudPhotoService(store, env.CacheDir);

        var target = Path.Combine(env.CacheDir, "item-1.bin");
        await service.WriteCacheFileAsync(target, MinimalPng, default);
        Assert.True(Directory.Exists(env.CacheDir));
        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(env.CacheDir, "*.tmp"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(env.CacheDir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        }

        // Cancelled write removes any .tmp file
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WriteCacheFileAsync(Path.Combine(env.CacheDir, "cancelled.bin"), MinimalPng, cts.Token));
        Assert.Empty(Directory.GetFiles(env.CacheDir, "*.tmp"));

        // Failing File.Move (destination is a directory) removes any .tmp file without throwing
        var blockingDir = Path.Combine(env.CacheDir, "dir-collision.bin");
        Directory.CreateDirectory(blockingDir);
        await service.WriteCacheFileAsync(blockingDir, MinimalPng, default);
        Assert.Empty(Directory.GetFiles(env.CacheDir, "*.tmp"));
    }

    [Fact]
    public async Task CloudOnlySyncedFilesRequireOptInRecheckBeforeReadAndHonorReadTimeout()
    {
        using var env = new TempPhotoEnv();
        var nfdFolder = Path.Combine(env.RootDir, "OneDrive-Kis\u0327isel");
        var nfcFolder = Path.Combine(env.RootDir, "OneDrive-Kişisel");
        Directory.CreateDirectory(nfdFolder);
        var cloudPhoto = Path.Combine(nfdFolder, "cloud-trip.jpg");
        var localPhoto = Path.Combine(nfdFolder, "local-shot.png");
        File.WriteAllBytes(cloudPhoto, MinimalJpeg);
        File.WriteAllBytes(localPhoto, MinimalPng);

        Assert.True(CloudPhotoService.IsPathInsideRoot(
            Path.Combine(nfdFolder, "cloud-trip.jpg"),
            nfcFolder,
            nfcFolder + Path.DirectorySeparatorChar) || !OperatingSystem.IsMacOS());

        var store = env.WriteConfig(new { });
        var readerCalls = 0;
        var forceAllCloudOnly = false;
        var stallRead = false;

        using var service = new CloudPhotoService(
            store,
            env.CacheDir,
            isCloudOnlyOverride: fi => forceAllCloudOnly || string.Equals(fi.Name, "cloud-trip.jpg", StringComparison.Ordinal),
            localFileReader: (path, allowDl) =>
            {
                readerCalls++;
                if (stallRead) Thread.Sleep(5000);
                return File.ReadAllBytes(path);
            },
            bodyReadTimeout: TimeSpan.FromMilliseconds(150));

        // 1. Default downloadCloudFiles=false skips cloud-only files and explains opt-in
        var cloudOnlySub = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "CloudOnly")).FullName;
        File.WriteAllBytes(Path.Combine(cloudOnlySub, "cloud-trip.jpg"), MinimalJpeg);

        var optOutOpts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            provider = "local",
            folder = cloudOnlySub,
        }));
        Assert.False(PhotoWidgetOptions.Parse(optOutOpts).DownloadCloudFiles);

        var optOutFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(optOutOpts), default);
        Assert.True(optOutFeed.Configured);
        Assert.Equal(1, optOutFeed.CloudOnlyCount);
        Assert.Empty(optOutFeed.Photos);
        Assert.Contains("Download cloud-only files on demand", optOutFeed.Error);

        // 2. If a local photo becomes cloud-only after feed discovery while downloadCloudFiles=false, read re-check refuses it
        var mixedOpts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            provider = "local",
            folder = nfdFolder,
            downloadCloudFiles = false,
        }));
        var mixedFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(mixedOpts), default);
        var localOnlyItem = Assert.Single(mixedFeed.Photos);
        forceAllCloudOnly = true;
        Assert.Null(await service.GetPhotoBytesAsync(localOnlyItem.Id, default));
        Assert.Equal(0, readerCalls);
        forceAllCloudOnly = false;

        // 3. Opt-in enabled times out stalled cloud materialization cleanly, then caches successful read in 0700/0600 cache
        var optInOpts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            provider = "local",
            folder = cloudOnlySub,
            downloadCloudFiles = true,
        }));
        var optInFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(optInOpts), default);
        var cloudItem = Assert.Single(optInFeed.Photos);

        stallRead = true;
        var sw = Stopwatch.StartNew();
        Assert.Null(await service.GetPhotoBytesAsync(cloudItem.Id, default));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Stalled read took too long: {sw.Elapsed}");

        stallRead = false;
        var loaded = await service.GetPhotoBytesAsync(cloudItem.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal("image/jpeg", loaded!.Value.Mime);
        Assert.True(File.Exists(Path.Combine(env.CacheDir, cloudItem.Id + ".bin")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(env.CacheDir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(env.CacheDir, cloudItem.Id + ".bin")));
        }
    }

    [Fact]
    public void DetectRasterMimeValidatesMagicBytesAndRejectsActiveMarkup()
    {
        Assert.Equal("image/png", CloudPhotoService.DetectRasterMime(MinimalPng));
        Assert.Equal("image/jpeg", CloudPhotoService.DetectRasterMime(MinimalJpeg));
        Assert.Equal("image/gif", CloudPhotoService.DetectRasterMime("GIF89a..."u8));
        Assert.Equal("image/webp", CloudPhotoService.DetectRasterMime("RIFF\x00\x00\x00\x00WEBPVP8 "u8));
        Assert.Equal("image/bmp", CloudPhotoService.DetectRasterMime("BM\x00\x00"u8));

        Assert.Null(CloudPhotoService.DetectRasterMime("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8));
        Assert.Null(CloudPhotoService.DetectRasterMime("<!DOCTYPE html><html></html>"u8));
        Assert.Null(CloudPhotoService.DetectRasterMime("hello world"u8));
    }
}
