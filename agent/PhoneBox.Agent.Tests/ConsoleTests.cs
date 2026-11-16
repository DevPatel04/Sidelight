using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ConsoleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-console-" + Guid.NewGuid().ToString("N"));

    public ConsoleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeWindowPlatform : IWindowPlatform
    {
        private nint _nextHandle = 100;
        public ConsoleWindowInfo? Foreground { get; set; }
        public List<ConsoleWindowInfo> Windows { get; set; } = [];
        public int GetForegroundCalls { get; private set; }
        public int FocusCalls { get; private set; }
        public int IsForegroundCalls { get; private set; }
        public string? LastFocusApp { get; private set; }
        public string? LastFocusTitle { get; private set; }
        public bool FocusResult { get; set; } = true;
        public Action<string, string?>? OnFocus { get; set; }

        public ConsoleWindowInfo? GetForeground(bool readTitle)
        {
            GetForegroundCalls++;
            if (Foreground is null) return null;
            return readTitle ? Foreground : Foreground with { Title = "" };
        }

        public IReadOnlyList<ConsoleWindowInfo> ListWindows() => Windows;

        public bool FocusWindow(string processMatch, string? titleMatch)
        {
            FocusCalls++;
            LastFocusApp = processMatch;
            LastFocusTitle = titleMatch;
            if (!FocusResult) return false;
            if (OnFocus is not null)
            {
                OnFocus(processMatch, titleMatch);
                return true;
            }
            var first = processMatch.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            Foreground = new ConsoleWindowInfo(first, titleMatch ?? "", ++_nextHandle, 4242, 999_999);
            return true;
        }

        public bool IsForeground(ConsoleWindowInfo target)
        {
            IsForegroundCalls++;
            if (Foreground is null) return false;
            if (target.Handle != 0 && Foreground.Handle != target.Handle) return false;
            if (target.Pid > 0 && Foreground.Pid != target.Pid) return false;
            if (target.StartTicks != 0 && Foreground.StartTicks != target.StartTicks) return false;
            return ConsoleService.MatchesProcess(Foreground.Process, target.Process);
        }
    }

    private sealed class FakeDeckNative : IDeckNative
    {
        public readonly List<string> Writes = [];
        public List<DeckSession> Sessions = [];
        public List<DeckMonitor> MonitorList = [];
        public int OutputVol = 40;
        public int MicVol = 70;

        public DeckAudio Audio() => new(true, null,
            [
                new("speaker", "Speaker", "output", true, OutputVol, false, 0, 2),
                new("mic", "Mic", "input", true, MicVol, false, 0, 1),
            ],
            Sessions.ToArray());

        public bool Device(string id, int? volume, bool? muted, int? channel = null)
        {
            Writes.Add($"device:{id}:{volume}");
            if (id == "mic" && volume is { } mv) MicVol = mv;
            if (id == "speaker" && volume is { } ov) OutputVol = ov;
            return true;
        }

        public bool Session(string id, int? volume, bool? muted)
        {
            Writes.Add($"session:{id}:{volume}");
            Sessions = Sessions.Select(s => s.Id == id
                ? s with { Volume = volume ?? s.Volume, Muted = muted ?? s.Muted }
                : s).ToList();
            return Sessions.Any(s => s.Id == id);
        }

        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => MonitorList;

        public bool Brightness(string id, int value)
        {
            Writes.Add($"brightness:{id}:{value}");
            MonitorList = MonitorList.Select(m => m.Id == id ? m with { Brightness = value } : m).ToList();
            return MonitorList.Any(m => m.Id == id);
        }

        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(false);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly FakeWindowPlatform Platform = new();
        public readonly FakeDeckNative Native = new();
        public readonly AudioControl Audio = new();
        public readonly MediaMonitor Media = new(NullLogger<MediaMonitor>.Instance);
        public readonly ObsMonitor Obs;
        public readonly PushStore Push;
        public readonly ControlDeck Deck;
        public readonly ConsoleService Service;
        public readonly ConfigStore Config;
        public readonly string ConfigPath;
        public readonly List<string> SentKeys = [];
        public readonly List<string> TypedTexts = [];
        public Action<string>? OnSendKeys { get; set; }
        public Action<string>? OnTypeText { get; set; }
        public long Now = 1_000;

        public Fixture(string dir, string json = "{}")
        {
            ConfigPath = Path.Combine(dir, $"cfg-{Guid.NewGuid():N}.json");
            File.WriteAllText(ConfigPath, json);
            Config = new ConfigStore(ConfigPath);
            Push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, $"data-{Guid.NewGuid():N}.json"));
            Obs = new ObsMonitor(Config, NullLogger<ObsMonitor>.Instance);
            Deck = new ControlDeck(Config, Native, NullLogger<ControlDeck>.Instance, () => Now, Push, Audio);
            Service = new ConsoleService(
                Config,
                NullLogger<ConsoleService>.Instance,
                Audio,
                Media,
                Obs,
                Deck,
                Push,
                Platform,
                () => Now,
                keys => { SentKeys.Add(keys); OnSendKeys?.Invoke(keys); },
                text => { TypedTexts.Add(text); OnTypeText?.Invoke(text); });
        }

        public void WriteConfig(string json)
        {
            File.WriteAllText(ConfigPath, json);
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddTicks(Random.Shared.Next(1, 1_000_000)));
        }

        public void Dispose()
        {
            Service.Dispose();
            Deck.Dispose();
            Push.Dispose();
        }
    }

    private static string ConsolePageJson(bool pageEnabled = true, bool control = true, string widgetType = "app-keypad") =>
        JsonSerializer.Serialize(new
        {
            id = "console-page",
            title = "Console",
            enabled = pageEnabled,
            widgets = new object[]
            {
                new { id = "w1", type = widgetType, x = 0, y = 0, w = 6, h = 4, options = new { control } },
            },
        });

    [Fact]
    public void NormalizeIsOffByDefaultWithEmptyProfilesAndClampsBounds()
    {
        var defaults = AppConsole.Normalize(null);
        Assert.False(defaults.Enabled);
        Assert.NotNull(defaults.Profiles);
        Assert.Empty(defaults.Profiles);

        var emptyPath = Path.Combine(_dir, "empty.json");
        File.WriteAllText(emptyPath, """{"console":{"profiles":[]}}""");
        var emptyCfg = new ConfigStore(emptyPath).Get();
        Assert.False(emptyCfg.Console.Enabled);
        Assert.Empty(emptyCfg.Console.Profiles!);

        var dirty = new AppConsoleConfig
        {
            Enabled = true,
            Profiles =
            [
                new ConsoleProfile
                {
                    Id = "",
                    Name = "İçerik Düzeni",
                    Color = "javascript:alert(1)",
                    Columns = 99,
                    Rows = 0,
                    IsDefault = true,
                    SubPages =
                    [
                        new ConsoleSubPage
                        {
                            Id = "",
                            Name = "Birinci",
                            Slots =
                            [
                                new ConsoleSlot { Kind = "powershell", Label = "Run", Keys = "Ctrl+B", Hint = "01234567890123456789EXTRA" },
                                new ConsoleSlot { Kind = "empty", Label = "X", Keys = "Ctrl+X", Target = "secret", Hint = "nope", Toggle = true, StateKey = "k" },
                            ],
                        },
                    ],
                    Axes =
                    [
                        new ConsoleAxis { Index = 99, Kind = "bad-kind", Sensitivity = "ultra", Name = "Dial" },
                        new ConsoleAxis { Index = 8, Kind = "mic-gain", Name = "Dup" },
                    ],
                },
                new ConsoleProfile { Id = "", Name = "Second", IsDefault = true, Columns = 1, Rows = 99 },
            ],
        };

        var normalized = AppConsole.Normalize(dirty);
        var first = normalized.Profiles![0];
        var second = normalized.Profiles[1];

        Assert.Equal("icerik-duzeni", first.Id);
        Assert.Equal("#38bdf8", first.Color);
        Assert.Equal(6, first.Columns);
        Assert.Equal(2, first.Rows);
        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);
        Assert.Equal(2, second.Columns);
        Assert.Equal(4, second.Rows);

        var s0 = first.SubPages![0].Slots![0];
        var s1 = first.SubPages[0].Slots![1];
        Assert.Equal("keys", s0.Kind);
        Assert.Equal(20, s0.Hint!.Length);
        Assert.Equal("empty", s1.Kind);
        Assert.Null(s1.Keys);
        Assert.Null(s1.Target);
        Assert.Null(s1.Hint);
        Assert.False(s1.Toggle);

        Assert.Single(first.Axes!);
        Assert.Equal(8, first.Axes![0].Index);
        Assert.Equal("keys", first.Axes[0].Kind);
        Assert.Equal("normal", first.Axes[0].Sensitivity);
    }

    [Fact]
    public void ToPublicAndStackOptionsEnforceAllowListsAndHideProfilesWhenDisabled()
    {
        var profile = new ConsoleProfile
        {
            Id = "obs",
            Name = "OBS",
            AppMatch = "obs64",
            TitleMatch = "Recording",
            SubPages =
            [
                new ConsoleSubPage
                {
                    Id = "main",
                    Name = "Main",
                    Slots =
                    [
                        new ConsoleSlot { Kind = "keys", Label = "Rec", Keys = "Ctrl+Shift+R", Hint = "Ctrl+Shift+R", StateKey = "obs:rec", Toggle = true },
                        new ConsoleSlot { Kind = "text", Label = "Snippet", Text = "secret token", Hint = "should hide" },
                        new ConsoleSlot { Kind = "macro", Label = "Macro", Steps = ["keys:Ctrl+A", "wait:50"] },
                        new ConsoleSlot { Kind = "shortcut", Label = "Run", Target = "secret-cmd" },
                    ],
                },
            ],
            Axes =
            [
                new ConsoleAxis { Index = 1, Kind = "keys", PrevKeys = "Ctrl+[", NextKeys = "Ctrl+]", TapAction = "Ctrl+0", AppTarget = "Spotify.exe" },
            ],
        };

        var disabledCfg = AppConsole.Normalize(new AppConsoleConfig { Enabled = false, Profiles = [profile] });
        var disabledJson = JsonSerializer.Serialize(AppConsole.ToPublic(disabledCfg), ConfigStore.WriteOpts);
        using var disabledDoc = JsonDocument.Parse(disabledJson);
        Assert.False(disabledDoc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Empty(disabledDoc.RootElement.GetProperty("profiles").EnumerateArray());

        var enabledCfg = AppConsole.Normalize(new AppConsoleConfig { Enabled = true, Profiles = [profile] });
        var json = JsonSerializer.Serialize(AppConsole.ToPublic(enabledCfg), ConfigStore.WriteOpts);
        using var doc = JsonDocument.Parse(json);

        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "keys", "text", "steps", "target", "appMatch", "titleMatch", "prevKeys", "nextKeys", "tapAction", "appTarget",
        };
        var foundProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPropertyNames(doc.RootElement, foundProps);

        foreach (var secretKey in forbidden)
            Assert.DoesNotContain(secretKey, foundProps);

        Assert.DoesNotContain("secret token", json);
        Assert.DoesNotContain("secret-cmd", json);
        Assert.DoesNotContain("should hide", json);

        var slot0 = doc.RootElement.GetProperty("profiles")[0].GetProperty("subPages")[0].GetProperty("slots")[0];
        Assert.Equal("Ctrl+Shift+R", slot0.GetProperty("hint").GetString());
        var slot1 = doc.RootElement.GetProperty("profiles")[0].GetProperty("subPages")[0].GetProperty("slots")[1];
        Assert.False(slot1.TryGetProperty("hint", out var h) && h.ValueKind != JsonValueKind.Null);
        var axis0 = doc.RootElement.GetProperty("profiles")[0].GetProperty("axes")[0];
        Assert.True(axis0.GetProperty("hasTap").GetBoolean());

        var stack = new WidgetConfig
        {
            Type = "stack",
            Options = new Dictionary<string, JsonElement>
            {
                ["widgets"] = JsonSerializer.SerializeToElement(new object[]
                {
                    new { id = "c1", type = "app-keypad", enabled = true, options = new { control = true, mode = "follow", leakedSecret = "nope" } },
                    new { id = "cmd1", type = "command", enabled = true, options = new { title = "Cmd", command = "PRIVATE" } },
                }),
            },
        };
        var sanitized = StackWidgets.PublicOptions(stack);
        var sanitizedJson = JsonSerializer.Serialize(sanitized);
        Assert.DoesNotContain("leakedSecret", sanitizedJson);
        Assert.DoesNotContain("PRIVATE", sanitizedJson);
        Assert.Contains("control", sanitizedJson);
    }

    [Fact]
    public void PollingStaysOffWhenConsoleIsDisabledOrNoEnabledConsoleWidgetExists()
    {
        var noWidgetJson = JsonSerializer.Serialize(new
        {
            @console = new
            {
                enabled = true,
                autoFollow = true,
                profiles = new object[] { new { id = "vscode", name = "VS Code", appMatch = "code" } },
            },
            pages = new object[]
            {
                new { id = "p0", enabled = true, widgets = new object[] { new { id = "n1", type = "note" } } },
                new { id = "p1", enabled = false, widgets = new object[] { new { id = "w1", type = "app-keypad" } } },
            },
        });
        using var f1 = new Fixture(_dir, noWidgetJson);
        f1.Platform.Foreground = new("Code.exe", "main.ts");
        f1.Service.Touch();
        f1.Service.StepPoll();
        Assert.Equal(0, f1.Platform.GetForegroundCalls);

        var disabledConsoleJson = JsonSerializer.Serialize(new
        {
            @console = new
            {
                enabled = false,
                autoFollow = true,
                profiles = new object[] { new { id = "vscode", name = "VS Code", appMatch = "code" } },
            },
            pages = new object[] { new { id = "p1", enabled = true, widgets = new object[] { new { id = "w1", type = "app-keypad", options = new { control = true } } } } },
        });
        using var f2 = new Fixture(_dir, disabledConsoleJson);
        f2.Platform.Foreground = new("Code.exe", "main.ts");
        f2.Service.Touch();
        f2.Service.StepPoll();
        Assert.Equal(0, f2.Platform.GetForegroundCalls);
    }

    [Fact]
    public void MatchProfileUsesExactProcessNamesNotSubstringsAndPrefersAppAndTitle()
    {
        var profiles = new List<ConsoleProfile>
        {
            new() { Id = "obs", Name = "OBS", AppMatch = "obs64" },
            new() { Id = "obs-rec", Name = "OBS Rec", AppMatch = "obs64", TitleMatch = "Recording" },
            new() { Id = "yt", Name = "YT", TitleMatch = "YouTube Studio" },
            new() { Id = "arc", Name = "Arc", AppMatch = "arc" },
            new() { Id = "vscode", Name = "VS Code", AppMatch = "code|cursor" },
            new() { Id = "disabled", Name = "Disabled", AppMatch = "notepad", Enabled = false },
        };

        Assert.Equal("obs", ConsoleService.MatchProfile(profiles, new("obs64.exe", "OBS 30 - Profile"))?.Id);
        Assert.Equal("obs-rec", ConsoleService.MatchProfile(profiles, new("obs64.exe", "OBS 30 - Recording"))?.Id);
        Assert.Equal("yt", ConsoleService.MatchProfile(profiles, new("chrome.exe", "Channel - YouTube Studio"))?.Id);
        Assert.Equal("arc", ConsoleService.MatchProfile(profiles, new("Arc.exe", "Tab"))?.Id);
        Assert.Equal("vscode", ConsoleService.MatchProfile(profiles, new("Code.exe", "main.cs"))?.Id);
        Assert.Null(ConsoleService.MatchProfile(profiles, new("SearchHost.exe", "Search")));
        Assert.Null(ConsoleService.MatchProfile(profiles, new("Xcode", "PhoneBox.xcodeproj")));
        Assert.Null(ConsoleService.MatchProfile(profiles, new("notepad.exe", "notes.txt")));
    }

    [Fact]
    public void StepPollAppliesAntiFlickerDebounceAndSnapshotOmitsActiveProcessAndTitle()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                profiles = new object[]
                {
                    new { id = "media", name = "Media", isDefault = true },
                    new { id = "vscode", name = "VS Code", appMatch = "code" },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("Code.exe", "secret-repo - Visual Studio Code");
        f.Service.StepPoll();
        var snap = f.Service.Snapshot();
        Assert.Equal("vscode", snap.MatchedProfileId);
        Assert.Equal("vscode", snap.EffectiveProfileId);

        var snapJson = JsonSerializer.Serialize(snap, ConfigStore.WriteOpts);
        Assert.DoesNotContain("activeProcess", snapJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activeTitle", snapJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-repo", snapJson);

        f.Platform.Foreground = new("explorer.exe", "Desktop");
        f.Now = 1_100;
        f.Service.StepPoll();
        Assert.Equal("vscode", f.Service.Snapshot().MatchedProfileId);

        f.Now = 1_549;
        f.Service.StepPoll();
        Assert.Equal("vscode", f.Service.Snapshot().MatchedProfileId);

        f.Now = 1_550;
        f.Service.StepPoll();
        Assert.Null(f.Service.Snapshot().MatchedProfileId);
        Assert.Equal("media", f.Service.Snapshot().EffectiveProfileId);
    }

    [Fact]
    public async Task MissingOrUnknownProfileOrSubPageNeverFallsBack()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "first",
                        name = "First",
                        isDefault = true,
                        appMatch = "code",
                        subPages = new object[]
                        {
                            new
                            {
                                id = "main",
                                name = "Main",
                                slots = new object[] { new { kind = "keys", label = "Save", keys = "Ctrl+S" } },
                            },
                        },
                        axes = new object[]
                        {
                            new { index = 1, name = "Vol", kind = "system-volume", tapAction = "deck:audio:mute" },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("code.exe", "main.cs", Handle: 11, Pid: 100, StartTicks: 1);
        f.Service.StepPoll();
        Assert.True((await f.Service.ActAsync(new("pin", Pinned: "first"), default)).Ok);

        // Missing / unknown profileId must fail rather than falling back to pinned/matched/default.
        var noProfileSlot = await f.Service.ActAsync(new("slot", ProfileId: null, SubPageId: "main", Slot: 0), default);
        Assert.False(noProfileSlot.Ok);
        Assert.Equal(400, noProfileSlot.StatusCode);

        var badProfileSlot = await f.Service.ActAsync(new("slot", ProfileId: "missing", SubPageId: "main", Slot: 0), default);
        Assert.False(badProfileSlot.Ok);
        Assert.Equal(404, badProfileSlot.StatusCode);

        var noSubSlot = await f.Service.ActAsync(new("slot", ProfileId: "first", SubPageId: null, Slot: 0), default);
        Assert.False(noSubSlot.Ok);
        Assert.Equal(400, noSubSlot.StatusCode);

        var badSubSlot = await f.Service.ActAsync(new("slot", ProfileId: "first", SubPageId: "wrong-sub", Slot: 0), default);
        Assert.False(badSubSlot.Ok);
        Assert.Equal(404, badSubSlot.StatusCode);

        var noProfileAxis = await f.Service.ActAsync(new("axis", ProfileId: null, Axis: 1, Delta: 1), default);
        Assert.False(noProfileAxis.Ok);
        Assert.Equal(400, noProfileAxis.StatusCode);

        var noAxisIdx = await f.Service.ActAsync(new("axis", ProfileId: "first", Axis: null, Delta: 1), default);
        Assert.False(noAxisIdx.Ok);
        Assert.Equal(400, noAxisIdx.StatusCode);

        var badAxisIdx = await f.Service.ActAsync(new("axis-tap", ProfileId: "first", Axis: 7), default);
        Assert.False(badAxisIdx.Ok);
        Assert.Equal(404, badAxisIdx.StatusCode);

        Assert.Empty(f.SentKeys);
    }

    [Fact]
    public async Task EmptyAppMatchRejectsKeysTextMacroAndKeyAxesEvenWhenDefaultOrTitleMatched()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "title-only",
                        name = "Title Only",
                        isDefault = true,
                        titleMatch = "YouTube",
                        subPages = new object[]
                        {
                            new
                            {
                                id = "main",
                                name = "Main",
                                slots = new object[]
                                {
                                    new { kind = "keys", label = "Space", keys = "Space" },
                                    new { kind = "text", label = "Type", text = "abc" },
                                    new { kind = "macro", label = "Macro", steps = new[] { "keys:K" } },
                                    new { kind = "deck", label = "Mute", target = "audio:mute" },
                                },
                            },
                        },
                        axes = new object[]
                        {
                            new { index = 1, name = "Seek", kind = "keys", prevKeys = "Left", nextKeys = "Right", tapAction = "K" },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("chrome.exe", "YouTube", Handle: 7, Pid: 77, StartTicks: 777);

        var resKeys = await f.Service.ActAsync(new("slot", ProfileId: "title-only", SubPageId: "main", Slot: 0), default);
        Assert.False(resKeys.Ok);
        Assert.Contains("process match", resKeys.Error!, StringComparison.OrdinalIgnoreCase);

        var resText = await f.Service.ActAsync(new("slot", ProfileId: "title-only", SubPageId: "main", Slot: 1), default);
        Assert.False(resText.Ok);

        var resMacro = await f.Service.ActAsync(new("slot", ProfileId: "title-only", SubPageId: "main", Slot: 2), default);
        Assert.False(resMacro.Ok);

        var resAxis = await f.Service.ActAsync(new("axis", ProfileId: "title-only", Axis: 1, Delta: 1), default);
        Assert.False(resAxis.Ok);

        var resTap = await f.Service.ActAsync(new("axis-tap", ProfileId: "title-only", Axis: 1), default);
        Assert.False(resTap.Ok);

        var resDeck = await f.Service.ActAsync(new("slot", ProfileId: "title-only", SubPageId: "main", Slot: 3), default);
        Assert.True(resDeck.Ok);

        Assert.Empty(f.SentKeys);
        Assert.Empty(f.TypedTexts);
    }

    [Fact]
    public async Task FocusBeforeKeysCapturesHandleAndAbortsIfFocusChangesAfterWaitOrMidMacro()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "obs",
                        name = "OBS Studio",
                        appMatch = "obs64",
                        subPages = new object[]
                        {
                            new
                            {
                                id = "main",
                                name = "Main",
                                slots = new object[]
                                {
                                    new { kind = "keys", label = "Save", keys = "Ctrl+B", toggle = true },
                                    new { kind = "text", label = "Type", text = "hello" },
                                    new { kind = "shortcut", label = "Missing", target = "missing-id" },
                                    new { kind = "macro", label = "Chain", steps = new[] { "keys:Ctrl+A", "wait:10", "text:rm -rf", "keys:Enter" } },
                                },
                            },
                        },
                        axes = new object[]
                        {
                            new { index = 1, name = "Frames", kind = "keys", prevKeys = "Left", nextKeys = "Right" },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("Finder", "", Handle: 1, Pid: 1, StartTicks: 1);
        f.Platform.FocusResult = false;

        var missing = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 0), default);
        Assert.False(missing.Ok);
        Assert.Equal("Start OBS Studio on the computer first", missing.Error);
        Assert.Equal(1, f.Platform.FocusCalls);
        Assert.Empty(f.SentKeys);

        // FocusWindow succeeds, but user Alt+Tabs to Terminal during the 80ms delay.
        f.Platform.FocusResult = true;
        f.Platform.OnFocus = (_, _) => f.Platform.Foreground = new("Terminal", "bash", Handle: 55, Pid: 55, StartTicks: 55);
        var hijacked = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 0), default);
        Assert.False(hijacked.Ok);
        Assert.Empty(f.SentKeys);

        // Normal focus succeeds and remembers handle across steps.
        f.Platform.OnFocus = null;
        var okKeys = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 0), default);
        Assert.True(okKeys.Ok);
        Assert.Equal(["Ctrl+B"], f.SentKeys);
        Assert.True(okKeys.State!.SlotStates["obs:main:0"]);

        var okText = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 1), default);
        Assert.True(okText.Ok);
        Assert.Equal(["hello"], f.TypedTexts);

        var errShortcut = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 2), default);
        Assert.False(errShortcut.Ok);

        // Mid-macro focus theft: step 1 sends Ctrl+A on OBS handle, then focus moves to another window (even another obs64 handle!).
        f.SentKeys.Clear();
        f.TypedTexts.Clear();
        f.OnSendKeys = k =>
        {
            if (k == "Ctrl+A")
                f.Platform.Foreground = new("obs64", "OBS", Handle: 999, Pid: 888, StartTicks: 777);
        };
        var stolenMacro = await f.Service.ActAsync(new("slot", ProfileId: "obs", SubPageId: "main", Slot: 3), default);
        Assert.False(stolenMacro.Ok);
        Assert.Contains("changed", stolenMacro.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Ctrl+A"], f.SentKeys);
        Assert.Empty(f.TypedTexts);

        // Mid-axis focus theft: first Right fires, then focus switches to Terminal before second Right.
        f.SentKeys.Clear();
        f.Platform.Foreground = new("obs64.exe", "OBS", Handle: 200, Pid: 200, StartTicks: 200);
        f.OnSendKeys = _ => f.Platform.Foreground = new("Terminal", "zsh", Handle: 201, Pid: 201, StartTicks: 201);
        var stolenAxis = await f.Service.ActAsync(new("axis", ProfileId: "obs", Axis: 1, Delta: 3), default);
        Assert.False(stolenAxis.Ok);
        Assert.Single(f.SentKeys);
    }

    [Fact]
    public async Task ActAsyncSerializesConcurrentCallsAndGuardsDisabledMidQueue()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "studio",
                        name = "Studio",
                        appMatch = "studio",
                        subPages = new object[]
                        {
                            new
                            {
                                id = "main",
                                name = "Main",
                                slots = new object[]
                                {
                                    new { kind = "macro", label = "M", steps = new[] { "keys:F1", "wait:120", "keys:F2" } },
                                    new { kind = "keys", label = "Single", keys = "F3" },
                                },
                            },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("studio.exe", "Studio", Handle: 10, Pid: 10, StartTicks: 10);

        var t1 = f.Service.ActAsync(new("slot", ProfileId: "studio", SubPageId: "main", Slot: 0), default);
        await Task.Delay(25);
        var t2 = f.Service.ActAsync(new("slot", ProfileId: "studio", SubPageId: "main", Slot: 1), default);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(["F1", "F2", "F3"], f.SentKeys);

        // If Console is disabled while a second call is queued behind _actGate, it does not execute.
        f.SentKeys.Clear();
        var t3 = f.Service.ActAsync(new("slot", ProfileId: "studio", SubPageId: "main", Slot: 0), default);
        await Task.Delay(25);
        f.WriteConfig(JsonSerializer.Serialize(new { pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement }, @console = new { enabled = false } }));
        var t4 = f.Service.ActAsync(new("slot", ProfileId: "studio", SubPageId: "main", Slot: 1), default);
        var r3 = await t3;
        var r4 = await t4;
        Assert.True(r3.Ok);
        Assert.False(r4.Ok);
        Assert.Equal(["F1", "F2"], f.SentKeys);
    }

    [Fact]
    public async Task SendInputUipiBlockIsReportedAsFailure()
    {
        var exAccessDenied = Assert.Throws<InvalidOperationException>(() => KeySender.CheckSendInputResult(0, 2, 5));
        Assert.Contains("UIPI", exAccessDenied.Message);
        var exZero = Assert.Throws<InvalidOperationException>(() => KeySender.CheckSendInputResult(0, 2, 0));
        Assert.Contains("UIPI", exZero.Message);
        KeySender.CheckSendInputResult(2, 2, 0);

        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "admin",
                        name = "AdminApp",
                        appMatch = "admin",
                        subPages = new object[] { new { id = "main", name = "Main", slots = new object[] { new { kind = "keys", label = "Run", keys = "F5" } } } },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Platform.Foreground = new("admin.exe", "Admin", Handle: 5, Pid: 5, StartTicks: 5);
        f.OnSendKeys = _ => throw new InvalidOperationException("Input was blocked by an elevated window (UIPI)");

        var res = await f.Service.ActAsync(new("slot", ProfileId: "admin", SubPageId: "main", Slot: 0), default);
        Assert.False(res.Ok);
        Assert.Equal(409, res.StatusCode);
        Assert.Contains("UIPI", res.Error!);
    }

    [Fact]
    public async Task AxesClampDeltaToFiveAndRateLimiterCapsBucketsAt256()
    {
        var cfgJson = JsonSerializer.Serialize(new
        {
            pages = new[] { JsonDocument.Parse(ConsolePageJson()).RootElement },
            @console = new
            {
                enabled = true,
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "studio",
                        name = "Studio",
                        appMatch = "studio",
                        axes = new object[]
                        {
                            new { index = 1, name = "Spotify", kind = "app-volume", appTarget = "Spotify", sensitivity = "normal" },
                            new { index = 2, name = "Mic", kind = "mic-gain", appTarget = "mic", sensitivity = "fine" },
                            new { index = 3, name = "Display", kind = "brightness", appTarget = "mon1", sensitivity = "coarse" },
                            new { index = 4, name = "Timeline", kind = "keys", prevKeys = "Left", nextKeys = "Right", tapAction = "Space", sensitivity = "normal" },
                        },
                    },
                },
            },
        });
        using var f = new Fixture(_dir, cfgJson);
        f.Native.Sessions = [new("sp-1", "Spotify", 42, "speaker", 50, false, 0)];
        f.Native.MonitorList = [new("mon1", "Primary", 60, 1920, 1080, 60)];
        for (var i = 0; i < 100 && f.Deck.CurrentMonitors().Count == 0; i++) await Task.Delay(10);
        f.Platform.Foreground = new("studio.exe", "Studio", Handle: 12, Pid: 12, StartTicks: 12);
        f.Service.StepPoll();

        // Delta 50 clamps to 5 * 3 = +15 (50 -> 65, not 100).
        var resVol = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 1, Delta: 50), default);
        Assert.True(resVol.Ok);
        Assert.Equal(65, f.Native.Sessions[0].Volume);

        var resMic = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 2, Delta: -1), default);
        Assert.True(resMic.Ok);
        Assert.Equal(69, f.Native.MicVol);

        var resBright = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 3, Delta: 1), default);
        Assert.True(resBright.Ok);
        Assert.Equal(66, f.Native.MonitorList[0].Brightness);

        var resKeys = await f.Service.ActAsync(new("axis", ProfileId: "studio", Axis: 4, Delta: 9), default);
        Assert.True(resKeys.Ok);
        Assert.Equal(5, f.SentKeys.Count);
        Assert.All(f.SentKeys, k => Assert.Equal("Right", k));

        var resTap = await f.Service.ActAsync(new("axis-tap", ProfileId: "studio", Axis: 4), default);
        Assert.True(resTap.Ok);
        Assert.Equal("Space", f.SentKeys.Last());

        // Rate limiter keeps <= 256 buckets and returns 429 on bursts > 30/s.
        for (var i = 0; i < 320; i++)
            Assert.True(f.Service.AllowRate($"client-{i}"));
        Assert.Equal(ConsoleService.MaxRateBuckets, f.Service.RateBucketCount);

        for (var i = 0; i < ConsoleService.RateLimitMaxRequests; i++)
            Assert.True(f.Service.AllowRate("spammer"));
        Assert.False(f.Service.AllowRate("spammer"));

        var rateDenied = await f.Service.ActAsync(new("pin", Pinned: "studio"), default, null, "spammer");
        Assert.False(rateDenied.Ok);
        Assert.Equal(429, rateDenied.StatusCode);

        f.Now += ConsoleService.RateLimitWindowMs;
        Assert.True(f.Service.AllowRate("spammer"));
    }

    [Fact]
    public async Task RealMiddlewareAndEndpointsEnforceOriginLanAuthDevicePageControlAndRateLimits()
    {
        const int localPort = 8765;
        const int lanPort = 8766;
        const string lanToken = "test-lan-token-123";

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certReq = new CertificateRequest("CN=PhoneBox Test", key, HashAlgorithmName.SHA256);
        using var lanCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        var cfgPath = Path.Combine(_dir, "server-cfg.json");
        string MakeCfgJson(bool consoleEnabled) => JsonSerializer.Serialize(new
        {
            lan = new { enabled = true, port = lanPort, token = lanToken, webhookToken = "wh-token" },
            phones = new object[]
            {
                new { id = "phone-no-console", name = "NoConsole", pages = new[] { "other" } },
                new { id = "phone-readonly", name = "ReadOnly", pages = new[] { "ro-page" } },
                new { id = "phone-control", name = "Control", pages = new[] { "ctl-page" } },
            },
            pages = new object[]
            {
                new { id = "other", title = "Other", enabled = true, widgets = new object[] { new { id = "n1", type = "note" } } },
                new { id = "ro-page", title = "RO", enabled = true, widgets = new object[] { new { id = "k1", type = "app-keypad", options = new { control = false } } } },
                new
                {
                    id = "ctl-page",
                    title = "Control",
                    enabled = true,
                    widgets = new object[]
                    {
                        new
                        {
                            id = "stk",
                            type = "stack",
                            options = new
                            {
                                widgets = new object[]
                                {
                                    new { id = "k2", type = "app-keypad", enabled = true, options = new { control = true } },
                                },
                            },
                        },
                    },
                },
            },
            @console = new
            {
                enabled = consoleEnabled,
                autoFollow = true,
                focusBeforeKeys = true,
                profiles = new object[]
                {
                    new
                    {
                        id = "studio",
                        name = "Studio",
                        appMatch = "studio",
                        subPages = new object[]
                        {
                            new { id = "main", name = "Main", slots = new object[] { new { kind = "keys", label = "Run", keys = "F5" } } },
                        },
                    },
                },
            },
        });
        File.WriteAllText(cfgPath, MakeCfgJson(consoleEnabled: false));
        var cfgStore = new ConfigStore(cfgPath);
        var platform = new FakeWindowPlatform { Foreground = new("studio.exe", "Studio", Handle: 30, Pid: 30, StartTicks: 30) };
        var sentKeys = new List<string>();
        long now = 10_000;
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "server-data.json"));
        var audio = new AudioControl();
        var media = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        var obs = new ObsMonitor(cfgStore, NullLogger<ObsMonitor>.Instance);
        using var deck = new ControlDeck(cfgStore, new FakeDeckNative(), NullLogger<ControlDeck>.Instance, () => now, push, audio);
        using var consoleSvc = new ConsoleService(cfgStore, NullLogger<ConsoleService>.Instance, audio, media, obs, deck, push,
            platform, () => now, k => sentKeys.Add(k), _ => { });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(cfgStore);
        builder.Services.AddSingleton(consoleSvc);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        await using var app = builder.Build();

        // Simulate localPort vs lanPort via X-Test-Port header so the real middleware runs both branches over Kestrel.
        app.Use((ctx, next) =>
        {
            ctx.Connection.LocalPort = ctx.Request.Headers.TryGetValue("X-Test-Port", out var p) && int.TryParse(p, out var parsed)
                ? parsed
                : localPort;
            return next();
        });
        app.UseAgentPathRewrite(localPort);
        app.UseRouting();
        app.UseAgentSecurity(cfgStore, localPort, [$"http://127.0.0.1:{localPort}", $"http://localhost:{localPort}"],
            lanPort, lanCert, NullLogger.Instance);
        app.MapConsoleEndpoints();

        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.Single();
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            HttpRequestMessage Req(HttpMethod method, string path, int port = localPort, string? origin = null,
                string? fetchSite = null, string? bearer = null, string? deviceId = null, object? body = null)
            {
                var req = new HttpRequestMessage(method, path);
                req.Headers.Host = $"127.0.0.1:{port}";
                req.Headers.Add("X-Test-Port", port.ToString());
                if (origin is not null) req.Headers.Add("Origin", origin);
                if (fetchSite is not null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
                if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (deviceId is not null) req.Headers.Add("X-PhoneBox-Device", deviceId);
                if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                return req;
            }

            // 1. Cross-site POST with Origin -> 403 Forbidden (even when feature is disabled).
            var evilOrigin = await client.SendAsync(Req(HttpMethod.Post, "/api/console/act",
                origin: "https://evil.example", body: new { kind = "pin", pinned = "studio" }));
            Assert.Equal(HttpStatusCode.Forbidden, evilOrigin.StatusCode);

            var foreignGet = await client.SendAsync(Req(HttpMethod.Get, "/api/console/state", fetchSite: "cross-site"));
            Assert.Equal(HttpStatusCode.Forbidden, foreignGet.StatusCode);

            // 2. Feature off -> 404 Not Found.
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, "/api/console/state"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act",
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }))).StatusCode);

            // Enable console on disk and advance timestamp.
            File.WriteAllText(cfgPath, MakeCfgJson(consoleEnabled: true));
            File.SetLastWriteTimeUtc(cfgPath, DateTime.UtcNow.AddMinutes(1));

            // 3. Unauthenticated LAN -> 403 Forbidden; /api/console/windows over LAN -> 403 Forbidden.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/console/state", port: lanPort, deviceId: "phone-control"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, deviceId: "phone-control",
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/console/windows", port: lanPort, bearer: lanToken, deviceId: "phone-control"))).StatusCode);

            // 4. Authenticated LAN missing X-PhoneBox-Device -> 403 Forbidden.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/console/state", port: lanPort, bearer: lanToken))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken,
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }))).StatusCode);

            // 5. Phone without any console widget on its assigned pages -> 403 Forbidden.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/console/state", port: lanPort, bearer: lanToken, deviceId: "phone-no-console"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken, deviceId: "phone-no-console",
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }))).StatusCode);

            // 6. Phone with read-only console widget (control: false) -> state 200 (no activeProcess/Title), act 403.
            var roStateResp = await client.SendAsync(Req(HttpMethod.Get, "/api/console/state", port: lanPort, bearer: lanToken, deviceId: "phone-readonly"));
            Assert.Equal(HttpStatusCode.OK, roStateResp.StatusCode);
            var roStateText = await roStateResp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("activeProcess", roStateText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("activeTitle", roStateText, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken, deviceId: "phone-readonly",
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }))).StatusCode);

            // 7. Phone with control: true inside Smart Stack -> missing/unknown ids return 400/404, valid act succeeds 200.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken, deviceId: "phone-control",
                body: new { kind = "slot", subPageId = "main", slot = 0 }))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken, deviceId: "phone-control",
                body: new { kind = "slot", profileId = "studio", subPageId = "missing", slot = 0 }))).StatusCode);

            var okResp = await client.SendAsync(Req(HttpMethod.Post, "/api/console/act", port: lanPort, bearer: lanToken, deviceId: "phone-control",
                body: new { kind = "slot", profileId = "studio", subPageId = "main", slot = 0 }));
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Equal(["F5"], sentKeys);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public void LanAllowlistExposesOnlyStateAndActNeverWindows()
    {
        Assert.True(Lan.AllowedOverLan("/api/console/state"));
        Assert.True(Lan.AllowedOverLan("/api/console/act"));
        Assert.False(Lan.AllowedOverLan("/api/console/windows"));

        Assert.False(Lan.IsWebhook("/api/console/state"));
        Assert.False(Lan.IsWebhook("/api/console/act"));
        Assert.False(Lan.IsWebhook("/api/console/windows"));
    }

    private static void CollectPropertyNames(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    names.Add(prop.Name);
                    CollectPropertyNames(prop.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectPropertyNames(item, names);
                break;
        }
    }
}
