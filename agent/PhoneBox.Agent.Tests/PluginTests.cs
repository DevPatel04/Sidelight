using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"phonebox-plugtest-{Guid.NewGuid():N}");
    private readonly List<PushStore> _pushes = [];

    private (ConfigStore Cfg, PushStore Push, PluginService Svc, string PluginsDir, string StatePath) CreateEnv(string? initialConfigJson = null)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfgPath = Path.Combine(dir, "config.json");
        var pushPath = Path.Combine(dir, "push.json");
        var statePath = Path.Combine(dir, "plugins.json");
        var pluginsDir = Path.Combine(dir, "plugins");
        if (initialConfigJson is not null) File.WriteAllText(cfgPath, initialConfigJson);

        var cfg = new ConfigStore(cfgPath);
        var push = new PushStore(NullLogger<PushStore>.Instance, pushPath);
        _pushes.Add(push);
        var svc = new PluginService(cfg, push, NullLogger<PluginService>.Instance, pluginsDir, statePath);
        return (cfg, push, svc, pluginsDir, statePath);
    }

    public void Dispose()
    {
        foreach (var p in _pushes) p.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static byte[] BuildZip(Dictionary<string, string> entries, Action<string, ZipArchiveEntry>? configureEntry = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                configureEntry?.Invoke(name, e);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<(WebApplication App, int LocalPort, int LanPort)> StartRealHttpHostAsync(ConfigStore cfg, PushStore push, PluginService svc)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var localPort = FreePort();
            var lanPort = FreePort();
            if (localPort == lanPort) continue;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, localPort);
                k.Listen(IPAddress.Loopback, lanPort);
            });
            builder.Services.AddSingleton(cfg);
            builder.Services.AddSingleton(push);
            builder.Services.AddSingleton(svc);
            var app = builder.Build();
            AgentHttp.UseSecurity(app, localPort, lanPort, [$"http://127.0.0.1:{localPort}", $"http://localhost:{localPort}"], cfg);
            AgentHttp.MapPluginEndpoints(app);
            try
            {
                await app.StartAsync();
                return (app, localPort, lanPort);
            }
            catch (IOException)
            {
                await app.DisposeAsync();
            }
        }
        throw new IOException("Could not bind test Kestrel ports");
    }

    [Fact]
    public async Task RealHttpMiddlewareBlocksManagementRoutesOverLanAndCrossOriginRequests()
    {
        var (cfg, push, svc, _, _) = CreateEnv("""
        {
          "lan": { "enabled": true, "port": 8766, "token": "lan-secret-token", "webhookToken": "wh-token" },
          "phones": [
            { "id": "phone-a", "name": "Phone A", "pages": ["p1"] },
            { "id": "phone-b", "name": "Phone B", "pages": ["p2"] }
          ],
          "pages": [
            { "id": "p1", "title": "P1", "enabled": true, "widgets": [{ "id": "w1", "type": "plugin:demo-plug", "x": 0, "y": 0, "w": 4, "h": 2 }] },
            { "id": "p2", "title": "P2", "enabled": true, "widgets": [] },
            { "id": "p3", "title": "P3", "enabled": false, "widgets": [{ "id": "w3", "type": "plugin:demo-plug", "x": 0, "y": 0, "w": 4, "h": 2 }] }
          ]
        }
        """);

        var (app, localPort, lanPort) = await StartRealHttpHostAsync(cfg, push, svc);
        await using var _ = app;
        using var http = new HttpClient();
        var localBase = $"http://127.0.0.1:{localPort}";
        var lanBase = $"http://127.0.0.1:{lanPort}";

        // 1. All /api/plugins/* management routes AND /api/plugin/{id}/act return 403 over LAN even with valid Bearer token
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/plugins"),
            (HttpMethod.Post, "/api/plugins/rescan"),
            (HttpMethod.Post, "/api/plugins/install"),
            (HttpMethod.Post, "/api/plugins/demo-plug/toggle"),
            (HttpMethod.Put, "/api/plugins/demo-plug/settings"),
            (HttpMethod.Delete, "/api/plugins/demo-plug"),
            (HttpMethod.Post, "/api/plugin/demo-plug/act"),
        })
        {
            using var req = new HttpRequestMessage(method, lanBase + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer lan-secret-token");
            using var res = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        // 2. Cross-origin / DNS-rebinding requests on local port return 403
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, localBase + "/api/plugins/install"))
        {
            crossOrigin.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            crossOrigin.Content = new StringContent("""{"path":"/tmp"}""", Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(crossOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        using (var crossSite = new HttpRequestMessage(HttpMethod.Post, localBase + "/api/plugins/demo-plug/toggle"))
        {
            crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            crossSite.Content = new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(crossSite);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        using (var dnsRebind = new HttpRequestMessage(HttpMethod.Get, localBase + "/api/plugins"))
        {
            dnsRebind.Headers.Host = $"evil.example:{localPort}";
            using var res = await http.SendAsync(dnsRebind);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        // 3. Same-origin local zip upload succeeds and leaves plugin disabled (Enabled == false)
        var zip = BuildZip(new()
        {
            ["plugin.json"] = """
            {
              "id": "demo-plug", "name": "Demo", "version": "1.0.0",
              "widgets": [{ "type": "plugin:demo-plug", "name": "Demo Card", "ui": { "kind": "stat", "data": { "title": "Hi", "value": "OK" } } }]
            }
            """,
        });
        using (var installReq = new HttpRequestMessage(HttpMethod.Post, localBase + "/api/plugins/install"))
        {
            installReq.Headers.TryAddWithoutValidation("Origin", localBase);
            installReq.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            installReq.Content = new ByteArrayContent(zip);
            installReq.Content.Headers.ContentType = new("application/zip");
            using var res = await http.SendAsync(installReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var installed = doc.RootElement.GetProperty("overview").GetProperty("installed")[0];
            Assert.False(installed.GetProperty("enabled").GetBoolean());
            Assert.False(installed.GetProperty("active").GetBoolean());
        }

        // Enable locally
        using (var toggleReq = new HttpRequestMessage(HttpMethod.Post, localBase + "/api/plugins/demo-plug/toggle"))
        {
            toggleReq.Headers.TryAddWithoutValidation("Origin", localBase);
            toggleReq.Content = new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(toggleReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // 4. LAN /api/plugin/{id}/state requires Bearer token and enforces PagesFor(deviceId) + enabled page
        using (var unauth = await http.GetAsync(lanBase + "/api/plugin/demo-plug/state?page=p1&widget=w1"))
            Assert.Equal(HttpStatusCode.Forbidden, unauth.StatusCode);

        using (var allowedReq = new HttpRequestMessage(HttpMethod.Get, lanBase + "/api/plugin/demo-plug/state?page=p1&widget=w1"))
        {
            allowedReq.Headers.TryAddWithoutValidation("Authorization", "Bearer lan-secret-token");
            allowedReq.Headers.TryAddWithoutValidation("X-PhoneBox-Device", "phone-a");
            using var res = await http.SendAsync(allowedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        using (var otherPhoneReq = new HttpRequestMessage(HttpMethod.Get, lanBase + "/api/plugin/demo-plug/state?page=p1&widget=w1"))
        {
            otherPhoneReq.Headers.TryAddWithoutValidation("Authorization", "Bearer lan-secret-token");
            otherPhoneReq.Headers.TryAddWithoutValidation("X-PhoneBox-Device", "phone-b");
            using var res = await http.SendAsync(otherPhoneReq);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("not_found", doc.RootElement.GetProperty("status").GetString());
        }

        using (var disabledPageReq = new HttpRequestMessage(HttpMethod.Get, lanBase + "/api/plugin/demo-plug/state?page=p3&widget=w3"))
        {
            disabledPageReq.Headers.TryAddWithoutValidation("Authorization", "Bearer lan-secret-token");
            using var res = await http.SendAsync(disabledPageReq);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("not_found", doc.RootElement.GetProperty("status").GetString());
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("exec")]
    [InlineData("bridge")]
    public void ManifestValidationRejectsHttpExecAndBridgePermissionsInLocalOnlyMode(string perm)
    {
        var m = new PluginManifest
        {
            Id = "demo-plug", Name = "Demo", Version = "1.0.0",
            Permissions = [perm],
            Widgets = [new PluginWidgetDef { Type = "plugin:demo-plug", Name = "Demo" }],
        };
        Assert.Equal(PluginManifestValidator.LocalOnlyRejectedMessage, PluginManifestValidator.Validate(m, null));
    }

    [Theory]
    [InlineData("http", "none")]
    [InlineData("command", "shell")]
    [InlineData("sidecar", "python3")]
    [InlineData("none", "node")]
    public void ManifestValidationRejectsNonLocalBackendKindsAndRuntimes(string kind, string runtime)
    {
        var m = new PluginManifest
        {
            Id = "demo-plug", Name = "Demo", Version = "1.0.0",
            Permissions = [],
            Backend = new PluginBackendDef { Kind = kind, Runtime = runtime },
            Widgets = [new PluginWidgetDef { Type = "plugin:demo-plug", Name = "Demo" }],
        };
        Assert.Equal(PluginManifestValidator.LocalOnlyRejectedMessage, PluginManifestValidator.Validate(m, null));
    }

    [Fact]
    public async Task ZipExtractionRejectsTraversalSymlinksReparsePointsAndLimits()
    {
        var (_, _, svc, pluginsDir, statePath) = CreateEnv();
        var validManifest = """{"id":"demo-plug","name":"Demo","version":"1.0.0","widgets":[{"type":"plugin:demo-plug","name":"Demo"}]}""";

        // 1. Zip-Slip traversal rejected
        var slipZip = BuildZip(new() { ["plugin.json"] = validManifest, ["../escape.txt"] = "pwn" });
        await using (var ms = new MemoryStream(slipZip))
            Assert.Contains("unsafe path", await svc.InstallFromZipAsync(ms));
        Assert.False(File.Exists(Path.Combine(pluginsDir, "escape.txt")));

        // 2. Unix symlink entry rejected
        var unixSymlinkZip = BuildZip(
            new() { ["plugin.json"] = validManifest, ["link.txt"] = "/etc/passwd" },
            (name, entry) => { if (name == "link.txt") entry.ExternalAttributes = 0xA1FF << 16; });
        await using (var ms = new MemoryStream(unixSymlinkZip))
            Assert.Contains("symbolic links", await svc.InstallFromZipAsync(ms));

        // 3. Windows reparse point entry rejected
        var reparseZip = BuildZip(
            new() { ["plugin.json"] = validManifest, ["reparse.txt"] = "x" },
            (name, entry) => { if (name == "reparse.txt") entry.ExternalAttributes = (int)FileAttributes.ReparsePoint; });
        await using (var ms = new MemoryStream(reparseZip))
            Assert.Contains("reparse points", await svc.InstallFromZipAsync(ms));

        // 4. >128 entries rejected
        var manyEntries = new Dictionary<string, string> { ["plugin.json"] = validManifest };
        for (var i = 0; i < 128; i++) manyEntries[$"f{i}.txt"] = "x";
        await using (var ms = new MemoryStream(BuildZip(manyEntries)))
            Assert.Contains("128 entry limit", await svc.InstallFromZipAsync(ms));

        // 5. >8 MB uncompressed total rejected
        var hugeZip = BuildZip(new() { ["plugin.json"] = validManifest, ["big.txt"] = new string('A', 9 * 1024 * 1024) });
        await using (var ms = new MemoryStream(hugeZip))
            Assert.Contains("8 MB limit", await svc.InstallFromZipAsync(ms));

        // 6. Valid zip installs disabled with 0700 dirs and 0600 files on Unix
        var validZip = BuildZip(new() { ["demo-plug/plugin.json"] = validManifest, ["demo-plug/sub/note.txt"] = "ok" });
        await using (var ms = new MemoryStream(validZip))
            Assert.Null(await svc.InstallFromZipAsync(ms));

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode dir0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode file0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(dir0700, File.GetUnixFileMode(Path.Combine(pluginsDir, "demo-plug")));
            Assert.Equal(dir0700, File.GetUnixFileMode(Path.Combine(pluginsDir, "demo-plug", "sub")));
            Assert.Equal(file0600, File.GetUnixFileMode(Path.Combine(pluginsDir, "demo-plug", "plugin.json")));
            Assert.Equal(file0600, File.GetUnixFileMode(statePath));
        }

        // 7. Folder install rejects filesystem symlinks on Unix
        if (!OperatingSystem.IsWindows())
        {
            var srcDir = Path.Combine(_root, "sym-src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "plugin.json"), validManifest);
            File.CreateSymbolicLink(Path.Combine(srcDir, "bad-link"), "/etc/hosts");
            Assert.Contains("symbolic links", await svc.InstallFromPathAsync(srcDir));
        }
    }

    [Fact]
    public async Task InstallStartsDisabledAndUpdateFlagsReapprovalUntilUserReenables()
    {
        var (_, _, svc, _, _) = CreateEnv();
        var v1Dir = Path.Combine(_root, "v1");
        Directory.CreateDirectory(v1Dir);
        File.WriteAllText(Path.Combine(v1Dir, "plugin.json"), """
        {
          "id": "note-plug", "name": "Note", "version": "1.0.0",
          "settings": [{ "key": "label", "label": "Label", "type": "text" }],
          "widgets": [{ "type": "plugin:note-plug", "name": "Note Card", "ui": { "kind": "html", "file": "card.html" } }]
        }
        """);
        File.WriteAllText(Path.Combine(v1Dir, "card.html"), "<div>v1</div>");

        Assert.Null(await svc.InstallFromPathAsync(v1Dir));
        using (var ov = ToJson(svc.GetOverview()))
        {
            var inst = ov.RootElement.GetProperty("installed")[0];
            Assert.False(inst.GetProperty("enabled").GetBoolean());
            Assert.False(inst.GetProperty("active").GetBoolean());
            Assert.False(inst.GetProperty("needsReapproval").GetBoolean());
        }
        Assert.DoesNotContain("plugin:note-plug", Catalog.CombinedJson(svc));

        Assert.Null(await svc.SetEnabledAsync("note-plug", true));
        Assert.Null(await svc.SaveSettingsAsync("note-plug", new() { ["label"] = JsonSerializer.SerializeToElement("keep-me") }));
        Assert.Contains("plugin:note-plug", Catalog.CombinedJson(svc));

        // Update content + version -> disables plugin, sets needsReapproval = true, keeps saved settings
        File.WriteAllText(Path.Combine(v1Dir, "plugin.json"), """
        {
          "id": "note-plug", "name": "Note", "version": "1.1.0",
          "settings": [{ "key": "label", "label": "Label", "type": "text" }],
          "widgets": [{ "type": "plugin:note-plug", "name": "Note Card", "ui": { "kind": "html", "file": "card.html" } }]
        }
        """);
        File.WriteAllText(Path.Combine(v1Dir, "card.html"), "<div>v2 updated</div>");
        Assert.Null(await svc.InstallFromPathAsync(v1Dir));

        using (var ov = ToJson(svc.GetOverview()))
        {
            var inst = ov.RootElement.GetProperty("installed")[0];
            Assert.False(inst.GetProperty("enabled").GetBoolean());
            Assert.False(inst.GetProperty("active").GetBoolean());
            Assert.True(inst.GetProperty("needsReapproval").GetBoolean());
            var summary = inst.GetProperty("changeSummary").GetString();
            Assert.Contains("1.0.0 to 1.1.0", summary);
            Assert.Contains("SHA-256", summary);
            Assert.Equal("keep-me", inst.GetProperty("settings").GetProperty("label").GetString());
        }
        Assert.DoesNotContain("plugin:note-plug", Catalog.CombinedJson(svc));

        // Blind enable without explicit re-approval fails; explicit re-approval succeeds
        Assert.Contains("approved", await svc.SetEnabledAsync("note-plug", true));
        Assert.Null(await svc.SetEnabledAsync("note-plug", true, approveChanges: true));
        using (var ov = ToJson(svc.GetOverview()))
        {
            var inst = ov.RootElement.GetProperty("installed")[0];
            Assert.True(inst.GetProperty("enabled").GetBoolean());
            Assert.True(inst.GetProperty("active").GetBoolean());
            Assert.False(inst.GetProperty("needsReapproval").GetBoolean());
        }
    }

    [Fact]
    public void PluginHtmlPreludeEnforcesStrictCspAndFrameIsolation()
    {
        using var stream = AppPaths.Resource("www/widgets/plugin.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var js = reader.ReadToEnd();
        Assert.Contains(
            """<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; form-action 'none'; frame-ancestors 'none'">""",
            js);
        Assert.Contains("e.source !== frame.contentWindow", js);
        Assert.Contains("'allow-scripts'", js);
    }

    [Fact]
    public async Task TemplatesJsonEscapeOptionsAndNeverInterpolateSecrets()
    {
        var (cfg, _, svc, _, _) = CreateEnv("""
        {
          "pages": [{
            "id": "p1", "title": "P1", "enabled": true,
            "widgets": [{
              "id": "w1", "type": "plugin:html-plug", "x": 0, "y": 0, "w": 4, "h": 3,
              "options": { "title": "</script><script>alert(1)</script>", "widgetSecret": "hidden-widget-opt" }
            }]
          }]
        }
        """);
        var src = Path.Combine(_root, "html-plug");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "plugin.json"), """
        {
          "id": "html-plug", "name": "HTML Plug", "version": "1.0.0",
          "settings": [
            { "key": "owner", "label": "Owner", "type": "text" },
            { "key": "apiKey", "label": "API Key", "type": "password", "secret": true }
          ],
          "widgets": [{
            "type": "plugin:html-plug", "name": "HTML",
            "ui": { "kind": "html", "file": "view.html" },
            "options": [
              { "key": "title", "label": "Title", "type": "text" },
              { "key": "widgetSecret", "label": "Widget Secret", "type": "password", "secret": true }
            ]
          }]
        }
        """);
        File.WriteAllText(Path.Combine(src, "view.html"), "<script>const opts={options};const title={options.title};const s={settings};const k={settings.apiKey};</script>");
        Assert.Null(await svc.InstallFromPathAsync(src));
        Assert.Null(await svc.SetEnabledAsync("html-plug", true));
        Assert.Null(await svc.SaveSettingsAsync("html-plug", new()
        {
            ["owner"] = JsonSerializer.SerializeToElement("<b>Bob</b>"),
            ["apiKey"] = JsonSerializer.SerializeToElement("super-secret-value"),
        }));

        using var st = ToJson(svc.GetWidgetState("html-plug", "p1", "w1"));
        var html = st.RootElement.GetProperty("ui").GetProperty("html").GetString()!;
        Assert.DoesNotContain("</script><script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("super-secret-value", html);
        Assert.DoesNotContain("hidden-widget-opt", html);
        Assert.Contains("""\u003C/script\u003E\u003Cscript\u003Ealert(1)\u003C/script\u003E""", html);
        Assert.Contains("""\u003Cb\u003EBob\u003C/b\u003E""", html);
        Assert.Contains("const k=null;", html);
    }

    [Fact]
    public async Task SecretSettingsAndWidgetOptionsAreScrubbedFromOverviewAndToPublic()
    {
        var (cfg, _, svc, _, _) = CreateEnv();
        var src = Path.Combine(_root, "sec-plug");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "plugin.json"), """
        {
          "id": "sec-plug", "name": "Sec Plug", "version": "1.0.0",
          "settings": [
            { "key": "label", "label": "Label", "type": "text" },
            { "key": "token", "label": "Token", "type": "password", "secret": true }
          ],
          "widgets": [{
            "type": "plugin:sec-plug.card", "name": "Sec Card",
            "options": [
              { "key": "title", "label": "Title", "type": "text" },
              { "key": "widgetSecret", "label": "Widget Secret", "type": "password", "secret": true }
            ]
          }]
        }
        """);
        Assert.Null(await svc.InstallFromPathAsync(src));
        Assert.Null(await svc.SetEnabledAsync("sec-plug", true));

        Assert.Null(await svc.SaveSettingsAsync("sec-plug", new()
        {
            ["label"] = JsonSerializer.SerializeToElement("Desk"),
            ["token"] = JsonSerializer.SerializeToElement("top-secret-token"),
        }));
        using (var ov = ToJson(svc.GetOverview()))
        {
            var inst = ov.RootElement.GetProperty("installed")[0];
            Assert.False(inst.GetProperty("settings").TryGetProperty("token", out _));
            Assert.Equal("token", inst.GetProperty("secretKeysSet")[0].GetString());
        }

        // Resaving with SecretPlaceholder keeps secret intact without returning it
        Assert.Null(await svc.SaveSettingsAsync("sec-plug", new()
        {
            ["label"] = JsonSerializer.SerializeToElement("Desk 2"),
            ["token"] = JsonSerializer.SerializeToElement(PluginService.SecretPlaceholder),
        }));
        using (var ov2 = ToJson(svc.GetOverview()))
        {
            var inst = ov2.RootElement.GetProperty("installed")[0];
            Assert.Equal("Desk 2", inst.GetProperty("settings").GetProperty("label").GetString());
            Assert.False(inst.GetProperty("settings").TryGetProperty("token", out _));
            Assert.Equal("token", inst.GetProperty("secretKeysSet")[0].GetString());
        }

        // Config.ToPublic() and StackWidgets.PublicOptions() scrub secret widget options
        Assert.Null(cfg.Save("""
        {
          "pages": [{
            "id": "p1", "title": "P1",
            "widgets": [
              { "id": "w1", "type": "plugin:sec-plug.card", "x": 0, "y": 0, "w": 4, "h": 2,
                "options": { "title": "Safe", "widgetSecret": "top-secret", "unknownKey": "drop-me" } },
              { "id": "st1", "type": "stack", "x": 4, "y": 0, "w": 4, "h": 2,
                "options": { "widgets": [{ "id": "c1", "type": "plugin:sec-plug.card", "options": { "title": "StackSafe", "widgetSecret": "stack-secret" } }] } }
            ]
          }]
        }
        """));

        using var pub = ToJson(cfg.Get().ToPublic());
        var widgetsEl = pub.RootElement.GetProperty("pages")[0].GetProperty("widgets");
        var topOpts = widgetsEl[0].GetProperty("options");
        Assert.Equal("Safe", topOpts.GetProperty("title").GetString());
        Assert.False(topOpts.TryGetProperty("widgetSecret", out _));
        Assert.False(topOpts.TryGetProperty("unknownKey", out _));

        var stackChildOpts = widgetsEl[1].GetProperty("options").GetProperty("widgets")[0].GetProperty("options");
        Assert.Equal("StackSafe", stackChildOpts.GetProperty("title").GetString());
        Assert.False(stackChildOpts.TryGetProperty("widgetSecret", out _));

        // Disabled plugin fails closed: StackWidgets.PublicOptions returns empty
        Assert.Null(await svc.SetEnabledAsync("sec-plug", false));
        using var pubDisabled = ToJson(cfg.Get().ToPublic());
        var disabledOpts = pubDisabled.RootElement.GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("options");
        Assert.False(disabledOpts.TryGetProperty("title", out _));
        Assert.False(disabledOpts.TryGetProperty("widgetSecret", out _));
    }

    private static readonly JsonSerializerOptions WebJsonOpts = new(JsonSerializerDefaults.Web);
    private static JsonDocument ToJson(object obj) => JsonDocument.Parse(JsonSerializer.Serialize(obj, WebJsonOpts));
}
