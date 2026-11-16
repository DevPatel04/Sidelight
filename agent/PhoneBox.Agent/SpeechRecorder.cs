using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

internal interface IActiveRecording : IAsyncDisposable
{
    string WavPath { get; }
    /// <summary>Stops recording and returns an error string if the capture failed, or null on success.</summary>
    Task<string?> StopAsync(CancellationToken ct = default);
    /// <summary>Aborts recording and deletes the temporary capture file.</summary>
    Task CancelAsync();
}

internal interface ISpeechAudioCapture
{
    bool HasPlatformCapture { get; }
    Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default);
}

/// <summary>macOS microphone capture via the Swift AVAudioRecorder helper for Apple Speech Recognition.</summary>
internal sealed class PlatformSpeechAudioCapture : ISpeechAudioCapture
{
    private readonly ISpeechAppleClient _appleHelper;

    public PlatformSpeechAudioCapture(ISpeechAppleClient appleHelper)
    {
        _appleHelper = appleHelper;
    }

    public bool HasPlatformCapture =>
        OperatingSystem.IsMacOS() && SpeechAppleHelper.IsToolchainOrBinaryAvailable();

    public async Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default)
    {
        SpeechStoragePermissions.EnsurePrivateAgentDir(Path.GetDirectoryName(wavPath)!);

        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("Speech recording in this build requires macOS Apple Speech Recognition.");

        var (exe, err) = await _appleHelper.EnsureBuiltAsync(ct);
        if (exe is null)
            throw new InvalidOperationException(err ?? "macOS speech helper is not available.");

        return await ProcessRecording.StartAppleHelperAsync(exe, wavPath, maxSec, onMaxReached, ct);
    }

    /// <summary>Updates WAV RIFF/data chunk lengths in place if a subprocess exited before flushing header sizes.</summary>
    internal static void RepairWavHeaderIfNeeded(string wavPath)
    {
        try
        {
            var info = new FileInfo(wavPath);
            if (!info.Exists || info.Length < 44) return;
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            Span<byte> riffHdr = stackalloc byte[12];
            if (fs.Read(riffHdr) < 12) return;
            if (riffHdr[0] != (byte)'R' || riffHdr[1] != (byte)'I' || riffHdr[2] != (byte)'F' || riffHdr[3] != (byte)'F') return;
            if (riffHdr[8] != (byte)'W' || riffHdr[9] != (byte)'A' || riffHdr[10] != (byte)'V' || riffHdr[11] != (byte)'E') return;

            var total = (uint)Math.Min(info.Length, uint.MaxValue);
            var currentRiffSize = BinaryPrimitives.ReadUInt32LittleEndian(riffHdr.Slice(4, 4));
            var expectedRiffSize = total - 8;

            Span<byte> chunkHdr = stackalloc byte[8];
            Span<byte> sizeBuf = stackalloc byte[4];
            long pos = 12;
            while (pos + 8 <= info.Length)
            {
                fs.Position = pos;
                if (fs.Read(chunkHdr) < 8) break;
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.Slice(4, 4));

                if (chunkHdr[0] == (byte)'d' && chunkHdr[1] == (byte)'a' && chunkHdr[2] == (byte)'t' && chunkHdr[3] == (byte)'a')
                {
                    var dataOffset = pos + 8;
                    if (total <= (uint)dataOffset) break;

                    var expectedDataSize = total - (uint)dataOffset;
                    var dataInvalid = chunkSize == 0 || chunkSize >= 0x7FFF_FFFFu || (ulong)dataOffset + chunkSize > total;
                    var riffInvalid = currentRiffSize == 0
                        || currentRiffSize >= 0x7FFF_FFFFu
                        || currentRiffSize > expectedRiffSize
                        || (ulong)currentRiffSize + 8 < (ulong)dataOffset + (dataInvalid ? expectedDataSize : chunkSize);

                    if (riffInvalid)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, expectedRiffSize);
                        fs.Position = 4;
                        fs.Write(sizeBuf);
                    }
                    if (dataInvalid)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, expectedDataSize);
                        fs.Position = pos + 4;
                        fs.Write(sizeBuf);
                    }
                    break;
                }

                // RIFF chunks are word-aligned (odd sizes have a trailing pad byte)
                var nextPos = pos + 8L + chunkSize + (chunkSize & 1u);
                if (nextPos <= pos || nextPos > info.Length) break;
                pos = nextPos;
            }
        }
        catch { }
    }
}

internal sealed class ProcessRecording : IActiveRecording
{
    private static readonly TimeSpan ApplePermissionStartupTimeout = TimeSpan.FromSeconds(65);
    private readonly Process _proc;
    private readonly StringBuilder _stderr = new();
    private readonly string _stopMode; // "apple-stdin"
    private readonly System.Threading.Timer _maxTimer;
    private int _stopping;

    public string WavPath { get; }

    internal ProcessRecording(Process proc, string wavPath, string stopMode, int maxSec, Action? onMaxReached)
    {
        _proc = proc;
        WavPath = wavPath;
        _stopMode = stopMode;

        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _stopping) == 0)
                onMaxReached?.Invoke();
        };

        _maxTimer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref _stopping) == 0)
                onMaxReached?.Invoke();
        }, null, TimeSpan.FromSeconds(Math.Clamp(maxSec, 5, 600) + 1), Timeout.InfiniteTimeSpan);
    }

    public static async Task<ProcessRecording> StartAppleHelperAsync(
        string exePath, string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = AppPaths.NeutralCwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        SpeechProcessEnv.ConfigureMinimal(psi);
        psi.ArgumentList.Add("record");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(maxSec.ToString());

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start macOS speech helper.");
        var rec = new ProcessRecording(proc, wavPath, "apple-stdin", maxSec, onMaxReached);
        var handedOff = false;

        try
        {
            rec.StartErrorPump();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ApplePermissionStartupTimeout);

            try
            {
                while (!timeoutCts.IsCancellationRequested)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token);
                    if (line is null) break;
                    if (line.Trim() == "READY")
                    {
                        _ = rec.DrainStdoutAsync();
                        handedOff = true;
                        return rec;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for macOS microphone to start (check Microphone permissions).");
            }

            ct.ThrowIfCancellationRequested();
            var errText = await rec.ReadErrorTextAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? "macOS microphone failed to start." : errText);
        }
        finally
        {
            if (!handedOff)
            {
                try { await rec.CancelAsync(); } catch { }
                try { await rec.DisposeAsync(); } catch { }
            }
        }
    }

    internal void StartErrorPump()
    {
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                if (_stderr.Length < 4096) _stderr.AppendLine(e.Data);
            }
        };
        _proc.BeginErrorReadLine();
    }

    internal async Task DrainStdoutAsync()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync() is not null) { }
        }
        catch { }
    }

    internal async Task<string> ReadErrorTextAsync()
    {
        try { await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        lock (_stderr) { return _stderr.ToString().Trim(); }
    }

    public async Task<string?> StopAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _stopping, 1);
        _maxTimer.Dispose();

        if (!_proc.HasExited)
        {
            try
            {
                if (_stopMode == "apple-stdin")
                {
                    await _proc.StandardInput.WriteLineAsync("STOP");
                    await _proc.StandardInput.FlushAsync();
                    _proc.StandardInput.Close();
                }
            }
            catch { }

            try
            {
                await _proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch
            {
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }

        PlatformSpeechAudioCapture.RepairWavHeaderIfNeeded(WavPath);
        SpeechStoragePermissions.EnsurePrivateFile(WavPath);

        if (!File.Exists(WavPath) || new FileInfo(WavPath).Length <= 44)
        {
            string err;
            lock (_stderr) { err = _stderr.ToString().Trim(); }
            return string.IsNullOrWhiteSpace(err) ? "No audio was captured." : err;
        }
        return null;
    }

    public Task CancelAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _maxTimer.Dispose();
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        try { File.Delete(WavPath); } catch { }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _maxTimer.Dispose();
        try { _proc.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
