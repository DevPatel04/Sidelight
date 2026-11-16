using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The app console sends a profile's keys only to the app it is for, and only when a widget allows it.</summary>
public sealed class AppConsoleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-console-{Guid.NewGuid():N}.json");
    private readonly List<string> _sent = [];
    private FocusedApp? _focused = new("Photoshop", false);

    private const string Profiles = """
        [{ "id": "ps", "label": "Photoshop", "apps": "photoshop.exe, gimp",
           "keys": [{ "label": "Undo", "keys": "ctrl+z" }, { "label": "Save both", "keys": "ctrl+s, ctrl+shift+s" }],
           "dialLeft": "minus", "dialRight": "equals" }]
        """;

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private AppConsole Make(bool control = true, bool pageEnabled = true)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "p", "title": "P", "enabled": {{(pageEnabled ? "true" : "false")}},
                "widgets": [ { "id": "c", "type": "app-console", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": {{(control ? "true" : "false")}} } } ] },
                { "id": "q", "title": "Q", "enabled": true, "widgets": [] } ],
              "console": { "profiles": {{Profiles}} } }
            """);
        return new AppConsole(new ConfigStore(_path), NullLogger<AppConsole>.Instance)
        {
            Supported = true,
            AgentElevated = false,
            Focused = () => _focused,
            SendCombo = combo => { _sent.Add(string.Join("+", combo)); return true; },
        };
    }

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body);

    [Fact]
    public void TheStateNamesTheProfileButNeverTheApp()
    {
        var (status, body) = Make().State(null);
        Assert.Equal(200, status);
        var json = Json(body).GetRawText();
        Assert.Contains("\"Id\":\"ps\"", json);
        Assert.Contains("Undo", json);
        Assert.DoesNotContain("ctrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photoshop.exe", json, StringComparison.OrdinalIgnoreCase);

        _focused = new("notepad", false);
        Assert.Equal(JsonValueKind.Null, Json(Make().State(null).Body).GetProperty("profile").ValueKind);
    }

    [Fact]
    public async Task KeysGoToTheMatchingApp()
    {
        var console = Make();
        Assert.Equal((200, (string?)null), await console.Act(null, "ps", "k1", 1, default));
        Assert.Equal(["ctrl+s", "ctrl+shift+s"], _sent);
        _focused = new("GIMP", false); // names match without case and without .exe
        Assert.Equal(200, (await console.Act(null, "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public async Task NothingIsSentWithoutAWidgetThatAllowsIt()
    {
        Assert.Equal(403, (await Make(control: false).Act(null, "ps", "k0", 1, default)).Status);
        Assert.Equal(403, (await Make(pageEnabled: false).Act(null, "ps", "k0", 1, default)).Status);
        Assert.Equal(404, Make(pageEnabled: false).State(null).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AnotherFocusedAppGetsNothing()
    {
        _focused = new("notepad", false);
        Assert.Equal(409, (await Make().Act(null, "ps", "k0", 1, default)).Status);
        _focused = null;
        Assert.Equal(409, (await Make().Act(null, "ps", "k0", 1, default)).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AFocusChangeMidMacroStopsTheRest()
    {
        var console = Make();
        var calls = 0;
        console.Focused = () => ++calls == 1 ? new FocusedApp("photoshop", false) : new FocusedApp("explorer", false);
        Assert.Equal(409, (await console.Act(null, "ps", "k1", 1, default)).Status);
        Assert.Equal(["ctrl+s"], _sent);
    }

    [Fact]
    public async Task AnElevatedAppIsRefusedWithAReason()
    {
        _focused = new("Photoshop", true);
        var (status, error) = await Make().Act(null, "ps", "k0", 1, default);
        Assert.Equal(409, status);
        Assert.Contains("administrator", error);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task UnknownProfilesAndSlotsNeverFallBack()
    {
        var console = Make();
        Assert.Equal(404, (await console.Act(null, "other", "k0", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "ps", "k7", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "ps", "k01", 1, default)).Status);
        Assert.Equal(404, (await console.Act(null, "ps", "dial-press", 1, default)).Status); // not set
        Assert.Equal(404, (await console.Act(null, "ps", "ctrl+z", 1, default)).Status);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task DialTurnsAreClamped()
    {
        var console = Make();
        await console.Act(null, "ps", "dial-right", 50, default);
        Assert.Equal(5, _sent.Count);
        _sent.Clear();
        await console.Act(null, "ps", "k0", 9, default); // a key is pressed once whatever the phone asks
        Assert.Single(_sent);
    }

    [Fact]
    public async Task AFailedKeyPressIsReported()
    {
        var console = Make();
        console.SendCombo = _ => false;
        Assert.Equal(500, (await console.Act(null, "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public async Task UnsupportedSystemsSaySo()
    {
        var console = Make();
        console.Supported = false;
        Assert.False(Json(console.State(null).Body).GetProperty("supported").GetBoolean());
        Assert.Equal(409, (await console.Act(null, "ps", "k0", 1, default)).Status);
    }

    [Fact]
    public void SavingChecksTheKeysAndNeedsAnApp()
    {
        File.WriteAllText(_path, "{}");
        var store = new ConfigStore(_path);
        Assert.Contains("unknown key", store.Save("""{ "console": { "profiles": [{ "label": "X", "apps": "x", "keys": [{ "keys": "ctrl+nope" }] }] } }"""));
        Assert.Contains("no app", store.Save("""{ "console": { "profiles": [{ "label": "X", "apps": " , ", "keys": [{ "keys": "f5" }] }] } }"""));
        Assert.Null(store.Save("""{ "console": { "profiles": [{ "label": "Edit Pro", "apps": "edit", "keys": [{ "keys": "f5" }], "dialLeft": " " }, { "label": "Edit Pro", "apps": "e2" }] } }"""));
        var saved = store.Get().Console.Profiles;
        Assert.Equal(["edit-pro", "edit-pro-2"], saved.Select(p => p.Id));
        Assert.Equal("f5", saved[0].Keys[0].Label); // an empty label shows the keys
        Assert.Null(saved[0].DialLeft);
    }

    [Fact]
    public void TheKeysNeverReachThePhoneConfig()
    {
        Make();
        var json = JsonSerializer.Serialize(new ConfigStore(_path).Get().ToPublic());
        Assert.DoesNotContain("ctrl+z", json);
        Assert.DoesNotContain("photoshop", json, StringComparison.OrdinalIgnoreCase);
    }
}
