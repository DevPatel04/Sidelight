using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace PhoneBox.Agent.Tests;

public sealed class SpeechTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "phonebox-speech-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<PushStore> _pushStores = [];

    public void Dispose()
    {
        foreach (var p in _pushStores)
        {
            try { p.Dispose(); } catch { }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PushStore CreatePushStore()
    {
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push-" + Guid.NewGuid().ToString("N") + ".json"));
        _pushStores.Add(store);
        return store;
    }

    private ConfigStore CreateStore(AgentConfig? custom = null, bool speechEnabled = true)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        var cfg = custom ?? new AgentConfig
        {
            Speech = new SpeechConfig
            {
                Enabled = speechEnabled,
                Provider = "apple",
                SaveTranscripts = false,
            },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "speech-1",
                            Type = "speech",
                            X = 0, Y = 0, W = 4, H = 4,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["title"] = JsonSerializer.SerializeToElement("Quick Dictate"),
                                ["provider"] = JsonSerializer.SerializeToElement("default"),
                                ["noteTarget"] = JsonSerializer.SerializeToElement("home/note-1"),
                                ["noteMode"] = JsonSerializer.SerializeToElement("append"),
                                ["autoAppendNote"] = JsonSerializer.SerializeToElement(false),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "note-1",
                            Type = "note",
                            X = 4, Y = 0, W = 4, H = 3,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["text"] = JsonSerializer.SerializeToElement("Write something here…"),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "stack-1",
                            Type = "stack",
                            X = 8, Y = 0, W = 4, H = 4,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["widgets"] = JsonSerializer.SerializeToElement(new object[]
                                {
                                    new
                                    {
                                        id = "s-inactive-note",
                                        type = "note",
                                        enabled = true,
                                        active = false,
                                        options = new { text = "Disabled card" },
                                    },
                                    new
                                    {
                                        id = "s-note",
                                        type = "note",
                                        enabled = true,
                                        options = new { text = "Existing line" },
                                    },
                                    new
                                    {
                                        id = "s-speech",
                                        type = "speech",
                                        enabled = true,
                                        options = new
                                        {
                                            title = "Stack Mic",
                                            mode = "hold",
                                            noteTarget = "home/stack-1:s-note",
                                            noteMode = "bullet",
                                            autoAppendNote = true,
                                            control = true,
                                        },
                                    },
                                }),
                            },
                        },
                    ],
                },
            ],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, ConfigStore.WriteOpts));
        return new ConfigStore(path);
    }

    private static void UpdateStore(ConfigStore store, Action<AgentConfig> mutate)
    {
        var cur = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(store.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts)!;
        mutate(cur);
        Assert.Null(store.Save(JsonSerializer.Serialize(cur, ConfigStore.WriteOpts)));
    }

    private sealed class FakeActiveRecording(string wavPath) : IActiveRecording
    {
        public string WavPath { get; } = wavPath;
        public bool ThrowOnStop { get; set; }
        public bool WasStopped { get; private set; }
        public bool WasCanceled { get; private set; }

        public Task<string?> StopAsync(CancellationToken ct = default)
        {
            WasStopped = true;
            if (ThrowOnStop)
                throw new InvalidOperationException("Simulated capture failure at " + WavPath);
            Directory.CreateDirectory(Path.GetDirectoryName(WavPath)!);
            File.WriteAllBytes(WavPath, BuildTestWav(1600));
            return Task.FromResult<string?>(null);
        }

        public Task CancelAsync()
        {
            WasCanceled = true;
            try { if (File.Exists(WavPath)) File.Delete(WavPath); } catch { }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : ISpeechAudioCapture
    {
        public bool HasPlatformCapture => true;
        public FakeActiveRecording? LastRecording { get; private set; }
        public Action? LastOnMaxReached { get; private set; }
        public bool NextThrowOnStop { get; set; }

        public Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default)
        {
            LastOnMaxReached = onMaxReached;
            LastRecording = new FakeActiveRecording(wavPath) { ThrowOnStop = NextThrowOnStop };
            return Task.FromResult<IActiveRecording>(LastRecording);
        }
    }

    private sealed class FakeAppleClient : ISpeechAppleClient
    {
        public bool IsSupported { get; set; } = true;
        public AppleSpeechStatus? CustomStatus { get; set; }
        public string NextTranscript { get; set; } = "Hello from Apple Speech";
        public Func<CancellationToken, Task<(string? Text, string? Error)>>? CustomTranscribe { get; set; }

        public Task<(string? Executable, string? Error)> EnsureBuiltAsync(CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>((IsSupported ? "/tmp/PhoneBoxSpeech" : null, IsSupported ? null : "macOS only"));

        public Task<AppleSpeechStatus> GetStatusAsync(string? locale, CancellationToken ct = default) =>
            Task.FromResult(CustomStatus ?? new AppleSpeechStatus(IsSupported, IsSupported, IsSupported ? "authorized" : "unsupported", IsSupported ? "authorized" : "unsupported"));

        public Task<(string? Text, string? Error)> TranscribeAsync(string wavPath, string? locale, bool onDevice, CancellationToken ct) =>
            CustomTranscribe is not null
                ? CustomTranscribe(ct)
                : Task.FromResult<(string?, string?)>((NextTranscript, null));
    }

    private static byte[] BuildTestWav(int pcmBytes)
    {
        var total = 44 + pcmBytes;
        var wav = new byte[total];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(total - 8).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16000).CopyTo(wav, 24);
        BitConverter.GetBytes(32000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(pcmBytes).CopyTo(wav, 40);
        return wav;
    }

    private static int FreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void SpeechConfigNormalize_DefaultsOffAndOnDeviceTrue_AndClampsValues()
    {
        var cfg = new SpeechConfig
        {
            Provider = "openai", // Stage 1 does not support cloud providers
            MaxRecordSec = 9999,
            HistoryLimit = 0,
            TranscriptFormat = "invalid-format",
            TranscriptDir = "bad\0path",
        };
        cfg.Normalize();

        Assert.False(cfg.Enabled);
        Assert.True(cfg.AppleOnDevice);
        Assert.Equal("auto", cfg.Provider);
        Assert.Equal(600, cfg.MaxRecordSec);
        Assert.Equal(15, cfg.HistoryLimit);
        Assert.Equal("daily-md", cfg.TranscriptFormat);
        Assert.Equal("", cfg.TranscriptDir);

        var deserialized = JsonSerializer.Deserialize<SpeechConfig>("{}");
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.AppleOnDevice);
    }

    [Fact]
    public void ToPublic_ExcludesSpeechConfigScrubsOptionsAndSkipsNonStringStackTypes()
    {
        var store = CreateStore(new AgentConfig
        {
            Speech = new SpeechConfig
            {
                Enabled = true,
                TranscriptDir = "/Users/secret/Transcripts",
            },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "s1",
                            Type = "speech",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["title"] = JsonSerializer.SerializeToElement("Mic"),
                                ["transcriptDir"] = JsonSerializer.SerializeToElement("/Users/secret/Folder"),
                                ["unexpectedSecret"] = JsonSerializer.SerializeToElement("should-not-leak"),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "st1",
                            Type = "stack",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["widgets"] = JsonSerializer.SerializeToElement(new object[]
                                {
                                    new { id = "bad", type = 17 },
                                    new
                                    {
                                        id = "c1",
                                        type = "speech",
                                        enabled = true,
                                        options = new
                                        {
                                            title = "Stack Mic",
                                            transcriptDir = "/Users/secret/StackFolder",
                                            customHidden = "secret",
                                            control = false,
                                        },
                                    },
                                }),
                            },
                        },
                    ],
                },
            ],
        });

        // NormalizeWidget must skip non-string stack child types (e.g. {"type": 17}) cleanly without throwing
        var badStackWidget = store.Get().Pages[0].Widgets![1];
        SpeechWidgetSettings.NormalizeWidget(badStackWidget);

        var json = JsonSerializer.Serialize(store.Get().ToPublic(), ConfigStore.WriteOpts);
        Assert.DoesNotContain("should-not-leak", json);
        Assert.DoesNotContain("/Users/secret", json);
        Assert.DoesNotContain("customHidden", json);
        using var pubDoc = JsonDocument.Parse(json);
        Assert.False(pubDoc.RootElement.TryGetProperty("speech", out _));

        var parsed = SpeechWidgetSettings.Parse(null);
        Assert.False(parsed.Control);
    }

    [Fact]
    public void LanAllowedOverLan_PermitsPhoneEndpointsAndBlocksSettingsEndpoints()
    {
        Assert.True(Lan.AllowedOverLan("/api/speech/state"));
        Assert.True(Lan.AllowedOverLan("/api/speech/start"));
        Assert.True(Lan.AllowedOverLan("/api/speech/stop"));
        Assert.True(Lan.AllowedOverLan("/api/speech/cancel"));
        Assert.True(Lan.AllowedOverLan("/api/speech/append-note"));
        Assert.True(Lan.AllowedOverLan("/api/speech/clear"));

        Assert.False(Lan.AllowedOverLan("/api/speech/setup-status"));
        Assert.False(Lan.AllowedOverLan("/api/speech/browse-dirs"));
        Assert.False(Lan.AllowedOverLan("/api/speech/test"));

        Assert.True(BrowserGuard.IsForeign("cross-site", "cors", "POST", "/api/speech/start"));
        Assert.True(BrowserGuard.IsForeign("same-site", "cors", "GET", "/api/speech/state"));
        Assert.False(BrowserGuard.IsForeign("same-origin", "cors", "POST", "/api/speech/start"));
    }

    [Fact]
    public void ProcessEnvConfigureMinimal_StripsSecretsWithoutGlobalMutation()
    {
        var key = "PHONEBOX_TEST_SECRET_KEY";
        var psi = new System.Diagnostics.ProcessStartInfo("echo");
        psi.Environment[key] = "super-secret-value";
        SpeechProcessEnv.ConfigureMinimal(psi, new Dictionary<string, string?> { ["PATH"] = "/usr/bin", [key] = "super-secret-value" });

        Assert.False(psi.UseShellExecute);
        Assert.False(psi.Environment.ContainsKey(key));
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);
    }

    [Fact]
    public async Task StoragePermissions_PreservesExistingDirsEnforcesStrictModesAndReportsRealWriteAccess()
    {
        if (OperatingSystem.IsWindows()) return;

        var existingDir = Path.Combine(_dir, "existing-shared-dir");
        Directory.CreateDirectory(existingDir);
        var mode0755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(existingDir, mode0755);

        // Selecting an existing directory must NEVER chmod it to 0700
        SpeechStoragePermissions.EnsurePrivateDir(existingDir);
        Assert.Equal(mode0755, File.GetUnixFileMode(existingDir));

        // Newly created agent/transcript subdirs get 0700, transcript & audio files get 0600
        var newSubDir = Path.Combine(existingDir, "new-sub");
        SpeechStoragePermissions.EnsurePrivateDirStrict(newSubDir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(newSubDir));
        Assert.Equal(mode0755, File.GetUnixFileMode(existingDir));

        var secretFile = Path.Combine(newSubDir, "note.txt");
        await SpeechStoragePermissions.WriteOrAppendPrivateStrictAsync(secretFile, "secret", appendMode: false, CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(secretFile));

        // Read-only directory must report Writable = false in BrowseDirectories without leaving probe files
        var roDir = Path.Combine(_dir, "readonly-dir");
        Directory.CreateDirectory(roDir);
        File.SetUnixFileMode(roDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var svc = new SpeechService(CreateStore(), CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), new FakeCapture(), null, Path.Combine(_dir, "tmp-ro"));
            var browse = svc.BrowseDirectories(roDir);
            Assert.True(browse.Ok);
            Assert.False(browse.Writable);
            Assert.Empty(Directory.GetFiles(roDir));
        }
        finally
        {
            File.SetUnixFileMode(roDir, mode0755);
        }
    }

    [Fact]
    public async Task DiskSaveFailure_RetainsTranscriptInMemoryAndAppendsNote()
    {
        var roDir = Path.Combine(_dir, "unwritable-transcripts");
        Directory.CreateDirectory(roDir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(roDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var store = CreateStore();
            UpdateStore(store, c =>
            {
                c.Speech.SaveTranscripts = true;
                c.Speech.TranscriptDir = roDir;
                c.Pages[0].Widgets![0].Options!["autoAppendNote"] = JsonSerializer.SerializeToElement(true);
            });
            using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient { NextTranscript = "Kept despite disk error" }, new FakeCapture(), null, Path.Combine(_dir, "tmp-diskerr"));
            Assert.True((await svc.StartAsync("home/speech-1")).Ok);
            var stopRes = await svc.StopAsync("home/speech-1");
            Assert.True(stopRes.Ok);

            var st = svc.GetState();
            var entry = Assert.Single(st.Entries);
            Assert.Equal("Kept despite disk error", entry.Text);
            Assert.Null(entry.SavedFile);
            Assert.True(entry.AppendedToNote);
            Assert.Contains("disk save failed", st.LastError ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Kept despite disk error", store.Get().Pages[0].Widgets![1].Options!["text"].GetString());
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(roDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void RepairWavHeaderIfNeeded_FixesRiffAndDataSizes_AndRespectsListChunks()
    {
        var raw = BuildTestWav(1600);
        BitConverter.GetBytes(0).CopyTo(raw, 4);
        BitConverter.GetBytes(0).CopyTo(raw, 40);
        var wavPath = Path.Combine(_dir, "unfinalized.wav");
        File.WriteAllBytes(wavPath, raw);

        PlatformSpeechAudioCapture.RepairWavHeaderIfNeeded(wavPath);

        var fixedBytes = File.ReadAllBytes(wavPath);
        Assert.Equal(raw.Length - 8, BitConverter.ToInt32(fixedBytes, 4));
        Assert.Equal(1600, BitConverter.ToInt32(fixedBytes, 40));

        // WAV with an odd-sized LIST chunk before data: LIST size at offset 40 must NOT be overwritten
        var listPayload = Encoding.ASCII.GetBytes("INAMabc"); // 7 bytes + 1 pad byte
        var pcm = new byte[1200];
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("RIFF"));
        ms.Write(BitConverter.GetBytes(0)); // unfinalized RIFF size
        ms.Write(Encoding.ASCII.GetBytes("WAVE"));
        ms.Write(Encoding.ASCII.GetBytes("fmt "));
        ms.Write(BitConverter.GetBytes(16));
        ms.Write(BuildTestWav(0), 20, 16);
        ms.Write(Encoding.ASCII.GetBytes("LIST"));
        ms.Write(BitConverter.GetBytes(listPayload.Length));
        ms.Write(listPayload);
        ms.WriteByte(0); // padding for odd LIST size
        var dataChunkOffset = (int)ms.Position;
        ms.Write(Encoding.ASCII.GetBytes("data"));
        ms.Write(BitConverter.GetBytes(0)); // unfinalized data size
        ms.Write(pcm);

        var listWavPath = Path.Combine(_dir, "list-prefixed.wav");
        File.WriteAllBytes(listWavPath, ms.ToArray());

        PlatformSpeechAudioCapture.RepairWavHeaderIfNeeded(listWavPath);
        var repairedList = File.ReadAllBytes(listWavPath);
        Assert.Equal(repairedList.Length - 8, BitConverter.ToInt32(repairedList, 4));
        Assert.Equal(listPayload.Length, BitConverter.ToInt32(repairedList, 40));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(repairedList, dataChunkOffset + 4));
    }

    [Fact]
    public async Task FeatureDisabled_AndControlFalse_AreRejected()
    {
        var capture = new FakeCapture();
        var apple = new FakeAppleClient();

        using (var disabledSvc = new SpeechService(CreateStore(speechEnabled: false), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp1")))
        {
            var r = await disabledSvc.StartAsync("home/speech-1");
            Assert.False(r.Ok);
            Assert.Equal(404, r.StatusCode);
        }

        var lockedCfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "speech-locked", Type = "speech", Options = new() }],
                },
            ],
        };
        using var lockedSvc = new SpeechService(CreateStore(lockedCfg, speechEnabled: true), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp2"));
        var lockedRes = await lockedSvc.StartAsync("home/speech-locked");
        Assert.False(lockedRes.Ok);
        Assert.Equal(403, lockedRes.StatusCode);
    }

    [Fact]
    public async Task StartStopAppendNote_SupportsModesSkipsInactiveStackRejectsEmptyTargetAndCapsLength()
    {
        var store = CreateStore();
        var capture = new FakeCapture();
        var apple = new FakeAppleClient { NextTranscript = "First note line" };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp-note"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stop1 = await svc.StopAsync("home/speech-1");
        Assert.True(stop1.Ok);

        var entry = Assert.Single(svc.GetState().Entries);
        Assert.Equal("First note line", entry.Text);
        Assert.False(entry.AppendedToNote);

        var app1 = svc.AppendEntryToNote(entry.Id, "home/speech-1");
        Assert.True(app1.Ok);
        Assert.Equal("First note line", store.Get().Pages[0].Widgets![1].Options!["text"].GetString());

        // Capping Note length at 16,000 chars preserves newly appended text
        UpdateStore(store, c =>
        {
            c.Pages[0].Widgets![1].Options!["text"] = JsonSerializer.SerializeToElement(new string('a', 15995));
        });
        apple.NextTranscript = "Tail words";
        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        Assert.True((await svc.StopAsync("home/speech-1")).Ok);
        var e2 = svc.GetState().Entries[0];
        Assert.True(svc.AppendEntryToNote(e2.Id, "home/speech-1").Ok);
        var cappedText = store.Get().Pages[0].Widgets![1].Options!["text"].GetString()!;
        Assert.Equal(SpeechService.MaxNoteTextLength, cappedText.Length);
        Assert.EndsWith("\nTail words", cappedText);

        // AutoAppend bullet into active stack Note (skips inactive s-inactive-note!)
        apple.NextTranscript = "Stack bullet item";
        Assert.True((await svc.StartAsync("home/stack-1:s-speech")).Ok);
        Assert.True((await svc.StopAsync("home/stack-1:s-speech")).Ok);

        var stackWidgetsJson = store.Get().Pages[0].Widgets![2].Options!["widgets"].GetRawText();
        Assert.Contains("Existing line\\n\\u2022 Stack bullet item", stackWidgetsJson);
        Assert.Contains("Disabled card", stackWidgetsJson);
        Assert.DoesNotContain("Disabled card\\n", stackWidgetsJson);

        // Empty noteTarget must NOT silently fall back to nearest Note widget
        var noTargetCfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "sp-empty-target",
                            Type = "speech",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["noteTarget"] = JsonSerializer.SerializeToElement(""),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "note-1",
                            Type = "note",
                            Options = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("Original") },
                        },
                    ],
                },
            ],
        };
        using var noTargetSvc = new SpeechService(CreateStore(noTargetCfg), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp-notarget"));
        Assert.True((await noTargetSvc.StartAsync("home/sp-empty-target")).Ok);
        Assert.True((await noTargetSvc.StopAsync("home/sp-empty-target")).Ok);
        var noTargetEntry = Assert.Single(noTargetSvc.GetState().Entries);
        var failAppend = noTargetSvc.AppendEntryToNote(noTargetEntry.Id, "home/sp-empty-target");
        Assert.False(failAppend.Ok);
        Assert.Equal(400, failAppend.StatusCode);
    }

    [Fact]
    public async Task DiskStorage_SupportsFormatsAudioCopyRejectsUnsafeDirAndScrubsSavedFileInJson()
    {
        var outDir = Path.Combine(_dir, "transcripts");
        foreach (var fmt in SpeechConfig.ValidTranscriptFormats)
        {
            var cfg = new AgentConfig
            {
                Speech = new SpeechConfig
                {
                    Enabled = true,
                    Provider = "apple",
                    SaveTranscripts = true,
                    TranscriptDir = outDir,
                    TranscriptFormat = fmt,
                    SaveAudioCopy = fmt == "per-item-txt",
                },
                Pages =
                [
                    new PageConfig
                    {
                        Id = "home",
                        Title = "Home",
                        Enabled = true,
                        Widgets =
                        [
                            new WidgetConfig
                            {
                                Id = "sp",
                                Type = "speech",
                                Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) },
                            },
                        ],
                    },
                ],
            };
            using var svc = new SpeechService(CreateStore(cfg), CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient { NextTranscript = "Saved item " + fmt }, new FakeCapture(), null, Path.Combine(_dir, "tmp-" + fmt));
            Assert.True((await svc.StartAsync("home/sp")).Ok);
            Assert.True((await svc.StopAsync("home/sp")).Ok);
            var st = svc.GetState();
            var dto = Assert.Single(st.Entries);
            Assert.False(string.IsNullOrWhiteSpace(dto.SavedFile));
            Assert.True(File.Exists(dto.SavedFile));
            Assert.False(string.IsNullOrWhiteSpace(dto.SavedFileDisplay));

            var wireJson = JsonSerializer.Serialize(st);
            Assert.DoesNotContain("savedFile\"", wireJson, StringComparison.OrdinalIgnoreCase);

            if (fmt == "per-item-txt")
                Assert.True(Directory.EnumerateFiles(Path.Combine(outDir, "audio"), "*.wav").Any());
        }

        Assert.False(SpeechService.IsSafeTranscriptDirectory("/"));
        Assert.False(SpeechService.IsSafeTranscriptDirectory("/etc"));
        Assert.False(SpeechService.IsSafeTranscriptDirectory("/System"));
    }

    [Fact]
    public async Task CancelAsync_PreventsLatePublicationAndNoteMutation()
    {
        var store = CreateStore();
        UpdateStore(store, c =>
        {
            c.Pages[0].Widgets![0].Options!["autoAppendNote"] = JsonSerializer.SerializeToElement(true);
        });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apple = new FakeAppleClient
        {
            CustomTranscribe = async ct =>
            {
                started.TrySetResult();
                await release.Task;
                return ("Ghost transcript after cancel", null);
            },
        };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-cancel"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stopTask = svc.StopAsync("home/speech-1");
        await started.Task;

        var cancelRes = await svc.CancelAsync("home/speech-1");
        Assert.True(cancelRes.Ok);

        release.TrySetResult();
        await stopTask;
        await Task.Delay(50);

        Assert.Empty(svc.GetState().Entries);
        Assert.Equal("idle", svc.GetState().Status);
        Assert.Equal("Write something here…", store.Get().Pages[0].Widgets![1].Options!["text"].GetString());
    }

    [Fact]
    public async Task CancelAsync_AfterTranscribeBeforePublish_PreventsNoteAndHistoryMutation()
    {
        var store = CreateStore();
        var outDir = Path.Combine(_dir, "late-cancel-transcripts");
        UpdateStore(store, c =>
        {
            c.Speech.SaveTranscripts = true;
            c.Speech.TranscriptDir = outDir;
            c.Speech.TranscriptFormat = "per-item-txt";
            c.Pages[0].Widgets![0].Options!["autoAppendNote"] = JsonSerializer.SerializeToElement(true);
        });

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hold _diskGate while transcription completes so RunTranscriptionJobAsync passes ct.ThrowIfCancellationRequested()
        // and blocks at SaveTranscriptToDiskAsync -> _diskGate.WaitAsync(ct), then cancel while blocked or right after!
        var apple = new FakeAppleClient { NextTranscript = "Late post-transcribe ghost" };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-late-cancel"));

        // Pre-fill note and ensure cancellation token is checked at publication boundary too
        Assert.True((await svc.StartAsync("home/speech-1")).Ok);

        // Cancel using CancelAsync right after transcription started via a slow CustomTranscribe that returns AFTER cancel,
        // ignoring ct (so ct.ThrowIfCancellationRequested fires or ownership check at publication catches it).
        apple.CustomTranscribe = async _ =>
        {
            saveStarted.TrySetResult();
            await releaseSave.Task;
            return ("Should never publish", null);
        };
        var stopTask = svc.StopAsync("home/speech-1");
        await saveStarted.Task;

        // Cancel while CustomTranscribe is in progress, then let CustomTranscribe return normally (non-canceled)
        Assert.True((await svc.CancelAsync("home/speech-1")).Ok);
        releaseSave.TrySetResult();
        await stopTask;
        await Task.Delay(50);

        Assert.Empty(svc.GetState().Entries);
        Assert.Equal("Write something here…", store.Get().Pages[0].Widgets![1].Options!["text"].GetString());
    }

    [Fact]
    public async Task DisablingSpeechOrRemovingWidgetMidRecording_CancelsActiveSession_AndAllowsCleanup()
    {
        // 1) ConfigStore change event disabling Speech cancels active recording & cleans temp
        var store = CreateStore();
        var capture = new FakeCapture();
        using (var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), capture, null, Path.Combine(_dir, "tmp-cfg1")))
        {
            Assert.True((await svc.StartAsync("home/speech-1")).Ok);
            Assert.Equal("recording", svc.GetState().Status);

            UpdateStore(store, c => c.Speech.Enabled = false);
            await svc.ReconcileConfigAsync();

            Assert.Equal("idle", svc.GetState().Status);
            Assert.True(capture.LastRecording!.WasCanceled);
        }

        // 2) Removing widget mid-recording cancels active recording
        var store2 = CreateStore();
        var capture2 = new FakeCapture();
        using (var svc2 = new SpeechService(store2, CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), capture2, null, Path.Combine(_dir, "tmp-cfg2")))
        {
            Assert.True((await svc2.StartAsync("home/speech-1")).Ok);
            UpdateStore(store2, c => c.Pages[0].Widgets!.RemoveAll(w => w.Id == "speech-1"));
            await svc2.ReconcileConfigAsync();
            Assert.Equal("idle", svc2.GetState().Status);
            Assert.True(capture2.LastRecording!.WasCanceled);
        }

        // 3) Startup sweeps leftover rec-*.wav files, and CancelAllForShutdownAsync stops active recording
        var tmpRoot3 = Path.Combine(_dir, "tmp-sweep");
        Directory.CreateDirectory(tmpRoot3);
        var staleWav = Path.Combine(tmpRoot3, "rec-stale123.wav");
        await File.WriteAllBytesAsync(staleWav, [1, 2, 3, 4]);

        var store3 = CreateStore();
        var capture3 = new FakeCapture();
        using var svc3 = new SpeechService(store3, CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), capture3, null, tmpRoot3);
        Assert.False(File.Exists(staleWav));

        Assert.True((await svc3.StartAsync("home/speech-1")).Ok);
        await svc3.CancelAllForShutdownAsync();
        Assert.Equal("idle", svc3.GetState().Status);
        Assert.True(capture3.LastRecording!.WasCanceled);

        // 4) Max-duration callback stops active recording even if caller isn't checking phone UI,
        // and raw on-disk config edits cancel active capture when reloaded
        var store4 = CreateStore();
        var capture4 = new FakeCapture();
        using var svc4 = new SpeechService(store4, CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient { NextTranscript = "Max timer done" }, capture4, null, Path.Combine(_dir, "tmp-max"));
        Assert.True((await svc4.StartAsync("home/speech-1")).Ok);
        capture4.LastOnMaxReached?.Invoke();
        for (var i = 0; i < 40 && svc4.GetState().Status != "idle"; i++)
            await Task.Delay(25);
        Assert.Equal("idle", svc4.GetState().Status);
        Assert.True(capture4.LastRecording!.WasStopped);
        Assert.Single(svc4.GetState().Entries);

        // Now start again and edit config on disk to disable control -> reload cancels capture cleanly
        Assert.True((await svc4.StartAsync("home/speech-1")).Ok);
        var cfgJson = JsonSerializer.Serialize(store4.Get(), ConfigStore.WriteOpts).Replace("\"control\": true", "\"control\": false");
        await File.WriteAllTextAsync(store4.Path, cfgJson);
        File.SetLastWriteTimeUtc(store4.Path, DateTime.UtcNow.AddSeconds(2));
        _ = store4.Get();
        await svc4.ReconcileConfigAsync();
        Assert.Equal("idle", svc4.GetState().Status);
        Assert.True(capture4.LastRecording!.WasCanceled);
    }

    [Fact]
    public async Task StopAsyncFailure_ResetsStateAndScrubsLocalPaths()
    {
        var capture = new FakeCapture { NextThrowOnStop = true };
        using var svc = new SpeechService(CreateStore(), CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), capture, null, Path.Combine(_dir, "tmp-err"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stopRes = await svc.StopAsync("home/speech-1");
        Assert.False(stopRes.Ok);
        Assert.Equal("idle", svc.GetState().Status);
        Assert.DoesNotContain("/tmp-", stopRes.Error ?? "");
        Assert.DoesNotContain(_dir, stopRes.Error ?? "");
    }

    [Fact]
    public async Task StartAppleHelperAsync_CleansUpSubprocessAndWavOnCancellation()
    {
        if (OperatingSystem.IsWindows()) return;

        var helper = Path.Combine(_dir, "fake-apple-helper.sh");
        var pidFile = Path.Combine(_dir, "helper.pid");
        var wavPath = Path.Combine(_dir, "rec-cancel-start.wav");
        await File.WriteAllTextAsync(helper,
            "#!/bin/sh\n" +
            $"echo $$ > \"{pidFile}\"\n" +
            "sleep 30\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllBytesAsync(wavPath, [1, 2, 3, 4]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRecording.StartAppleHelperAsync(helper, wavPath, 60, null, cts.Token));

        Assert.False(File.Exists(wavPath));
        if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
        {
            await Task.Delay(100);
            var exited = true;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                exited = p.HasExited;
            }
            catch (ArgumentException) { exited = true; }
            Assert.True(exited);
        }
    }

    [Fact]
    public async Task TestAppleSpeech_RejectsDeniedPermissionsAndMissingOnDeviceModel()
    {
        var store = CreateStore();
        var apple = new FakeAppleClient
        {
            CustomStatus = new AppleSpeechStatus(true, true, "denied", "authorized"),
        };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-auth"));

        var deniedRes = await svc.TestProviderAsync(new SpeechConfig { Enabled = true, Provider = "apple", AppleOnDevice = true });
        Assert.False(deniedRes.Ok);
        Assert.Contains("denied", deniedRes.Message, StringComparison.OrdinalIgnoreCase);

        apple.CustomStatus = new AppleSpeechStatus(true, true, "authorized", "restricted");
        var micRestricted = await svc.TestProviderAsync(new SpeechConfig { Enabled = true, Provider = "apple", AppleOnDevice = true });
        Assert.False(micRestricted.Ok);
        Assert.Contains("restricted", micRestricted.Message, StringComparison.OrdinalIgnoreCase);

        apple.CustomStatus = new AppleSpeechStatus(true, false, "authorized", "authorized");
        var noModel = await svc.TestProviderAsync(new SpeechConfig { Enabled = true, Provider = "apple", AppleOnDevice = true, Language = "xx-XX" });
        Assert.False(noModel.Ok);
        Assert.Contains("On-device", noModel.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateWidgetIdsAcrossPages_AreIsolatedAndUnqualifiedIsRejected()
    {
        var cfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "speech", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
                new PageConfig
                {
                    Id = "work",
                    Title = "Work",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "speech", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
            ],
        };

        var apple = new FakeAppleClient { NextTranscript = "Work item" };
        using var svc = new SpeechService(CreateStore(cfg), CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-dup"));

        var ambig = await svc.StartAsync("speech");
        Assert.False(ambig.Ok);
        Assert.Equal(400, ambig.StatusCode);

        Assert.True((await svc.StartAsync("home/speech")).Ok);
        apple.NextTranscript = "Home item";
        Assert.True((await svc.StopAsync("home/speech")).Ok);

        Assert.True((await svc.StartAsync("work/speech")).Ok);
        apple.NextTranscript = "Work item";
        Assert.True((await svc.StopAsync("work/speech")).Ok);

        Assert.False(SpeechService.WidgetIdMatches("home/speech", "work/speech", "work/speech"));
        Assert.False(SpeechService.WidgetIdMatches("home/speech", "speech", "work/speech"));

        Assert.True(svc.Clear("work/speech").Ok);
        var remaining = Assert.Single(svc.GetState().Entries);
        Assert.Equal("home/speech", remaining.WidgetId);
    }

    [Fact]
    public void RateLimiter_BoundsBucketsAt256AndPreservesLiveRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new SpeechRateLimiter(maxRequests: 3, window: TimeSpan.FromSeconds(10), clock: () => now);

        // Client A sends 1 request at t=0, then 2 at t=6 (so at t=11, 1 expired and 2 live)
        Assert.True(limiter.TryAcquire("client-a"));
        now = now.AddSeconds(6);
        Assert.True(limiter.TryAcquire("client-a"));
        Assert.True(limiter.TryAcquire("client-a"));
        Assert.False(limiter.TryAcquire("client-a")); // 3 live

        // Advance to t=11 (first expired, 2 still live) and flood 270 other keys
        now = now.AddSeconds(5);
        for (var i = 0; i < 270; i++)
            _ = limiter.TryAcquire("other-" + i);

        Assert.True(limiter.BucketCount <= SpeechRateLimiter.MaxBuckets);
        // Client A still has 2 live requests -> 1 more allowed, then 4th rejected
        Assert.True(limiter.TryAcquire("client-a"));
        Assert.False(limiter.TryAcquire("client-a"));
    }

    [Fact]
    public async Task KestrelEndpoints_EnforceDisabledDeviceHeaderScopeAndStopCancelQuotaExemption()
    {
        var localPort = FreeLoopbackPort();
        var lanPort = FreeLoopbackPort();
        while (lanPort == localPort) lanPort = FreeLoopbackPort();

        var cfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = false, Provider = "apple" },
            Lan = new LanConfig { Enabled = true, Port = lanPort, Token = "lan-token", WebhookToken = "wh-token" },
            Phones =
            [
                new PhoneConfig { Id = "dev-speech", Name = "Allowed Phone", Pages = ["home"] },
                new PhoneConfig { Id = "dev-other", Name = "Other Phone", Pages = ["other"] },
            ],
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "sp", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
                new PageConfig
                {
                    Id = "other",
                    Title = "Other",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "clk", Type = "clock" }],
                },
            ],
        };

        var store = CreateStore(cfg);
        var push = CreatePushStore();
        var endpoint = new AgentEndpoint(localPort);
        var limiter = new SpeechRateLimiter(maxRequests: 2, window: TimeSpan.FromMinutes(1));
        using var speech = new SpeechService(store, push, endpoint, new FakeAppleClient(), new FakeCapture(), limiter, Path.Combine(_dir, "tmp-kestrel"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, localPort);
            k.Listen(IPAddress.Loopback, lanPort);
        });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(push);
        builder.Services.AddSingleton(endpoint);
        builder.Services.AddSingleton(speech);

        await using var app = builder.Build();
        SpeechEndpoints.Map(app, localPort);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient();

            // 1) Feature disabled -> 404
            var disabledGet = await client.GetAsync($"http://127.0.0.1:{localPort}/api/speech/state");
            Assert.Equal(HttpStatusCode.NotFound, disabledGet.StatusCode);

            UpdateStore(store, c => c.Speech.Enabled = true);

            // 2) LAN missing X-PhoneBox-Device header -> 403
            var missingDevLan = await client.GetAsync($"http://127.0.0.1:{lanPort}/api/speech/state");
            Assert.Equal(HttpStatusCode.Forbidden, missingDevLan.StatusCode);

            // 3) LAN from phone without speech page -> 403
            using var wrongPhoneReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lanPort}/api/speech/state");
            wrongPhoneReq.Headers.Add("X-PhoneBox-Device", "dev-other");
            var wrongPhoneRes = await client.SendAsync(wrongPhoneReq);
            Assert.Equal(HttpStatusCode.Forbidden, wrongPhoneRes.StatusCode);

            // 4) LAN from assigned phone -> 200
            using var okLanReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lanPort}/api/speech/state");
            okLanReq.Headers.Add("X-PhoneBox-Device", "dev-speech");
            var okLanRes = await client.SendAsync(okLanReq);
            Assert.Equal(HttpStatusCode.OK, okLanRes.StatusCode);

            // 5) Exhaust shared action quota -> 3rd clear/start gets 429, while stop & cancel remain exempt!
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/start?widgetId=home%2Fsp", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/clear?widgetId=home%2Fsp", null)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/clear?widgetId=home%2Fsp", null)).StatusCode);

            // Stop MUST bypass action quota and succeed!
            var stopRes = await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/stop?widgetId=home%2Fsp", null);
            Assert.Equal(HttpStatusCode.OK, stopRes.StatusCode);
            var cancelRes = await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/cancel?widgetId=home%2Fsp", null);
            Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task AppleSpeechHelper_CompilesOnMacOs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var helper = new SpeechAppleHelper();
        var (exe, err) = await helper.EnsureBuiltAsync();
        Assert.Null(err);
        Assert.False(string.IsNullOrWhiteSpace(exe));
        Assert.True(File.Exists(exe));
    }
}
