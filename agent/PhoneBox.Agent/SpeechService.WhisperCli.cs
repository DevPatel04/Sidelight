using System.Diagnostics;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private enum WhisperCliFlavor
    {
        WhisperCpp,
        MlxWhisper,
        PythonWhisper,
    }

    internal static string? DetectWhisperCli(SpeechConfig cfg)
    {
        var (path, _) = ResolveWhisperCliExecutable(cfg);
        return path;
    }

    internal static TimeSpan ComputeWhisperCliTimeout(int maxRecordSec)
    {
        var sec = Math.Max(180, Math.Clamp(maxRecordSec, 5, 600) * 2 + 120);
        return TimeSpan.FromSeconds(sec);
    }

    internal static bool IsDisallowedScriptWrapper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static WhisperCliFlavor ClassifyWhisperCli(string cliPath)
    {
        var name = Path.GetFileNameWithoutExtension(cliPath).ToLowerInvariant();
        if (name.Contains("mlx_whisper", StringComparison.Ordinal)) return WhisperCliFlavor.MlxWhisper;
        if (name == "whisper") return WhisperCliFlavor.PythonWhisper;
        return WhisperCliFlavor.WhisperCpp;
    }

    internal static (string? Path, string? Error) ResolveWhisperCliExecutable(SpeechConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.WhisperCliPath))
        {
            var expanded = ExpandPath(cfg.WhisperCliPath);
            if (IsDisallowedScriptWrapper(expanded))
                return (null, "Batch script wrappers (.bat / .cmd) cannot be used as the Whisper CLI binary.");
            if (File.Exists(expanded))
                return (expanded, null);

            var foundCustom = ToolProcess.Find(cfg.WhisperCliPath);
            if (foundCustom is not null)
            {
                if (IsDisallowedScriptWrapper(foundCustom))
                    return (null, "Batch script wrappers (.bat / .cmd) cannot be used as the Whisper CLI binary.");
                return (foundCustom, null);
            }
            return (null, $"Whisper CLI binary was not found at '{cfg.WhisperCliPath}'.");
        }

        foreach (var candidate in new[] { "mlx_whisper", "whisper-cli", "whisper-cpp", "whisper" })
        {
            var path = ToolProcess.Find(candidate);
            if (path is not null && !IsDisallowedScriptWrapper(path))
                return (path, null);
        }
        return (null, "Whisper CLI binary was not found on this computer (checked mlx_whisper, whisper-cli, whisper-cpp, whisper).");
    }

    /// <summary>Auto mode only considers Whisper CLI when the user configured a CLI path or model (and never a model-less whisper.cpp).</summary>
    internal static string? ResolveWhisperCliForAuto(SpeechConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.WhisperCliPath) && string.IsNullOrWhiteSpace(cfg.WhisperModelPath))
            return null;

        var (cli, _) = ResolveWhisperCliExecutable(cfg);
        if (cli is null) return null;

        if (ClassifyWhisperCli(cli) == WhisperCliFlavor.WhisperCpp)
        {
            var expandedModel = ExpandPath(cfg.WhisperModelPath);
            if (string.IsNullOrWhiteSpace(expandedModel) || !File.Exists(expandedModel))
                return null;
        }
        return cli;
    }

    internal static SpeechTestResult TestWhisperCli(SpeechConfig cfg)
    {
        var (cli, cliErr) = ResolveWhisperCliExecutable(cfg);
        if (cli is null)
            return new SpeechTestResult(false, "whisper-cli", cliErr ?? "No Whisper CLI found.");

        var flavor = ClassifyWhisperCli(cli);
        var expandedModel = ExpandPath(cfg.WhisperModelPath);
        if (flavor == WhisperCliFlavor.WhisperCpp)
        {
            if (string.IsNullOrWhiteSpace(expandedModel))
                return new SpeechTestResult(false, "whisper-cli", "whisper.cpp requires a local GGML model file path in 'Whisper model path or repo'.");
            if (!File.Exists(expandedModel))
                return new SpeechTestResult(false, "whisper-cli", $"Whisper GGML model file was not found at '{expandedModel}'.");
        }
        else if (!string.IsNullOrWhiteSpace(cfg.WhisperModelPath)
            && (cfg.WhisperModelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                || cfg.WhisperModelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathRooted(cfg.WhisperModelPath)))
        {
            if (!File.Exists(expandedModel) && !Directory.Exists(expandedModel))
                return new SpeechTestResult(false, "whisper-cli", $"Whisper model not found at '{expandedModel}'.");
        }

        return new SpeechTestResult(true, "whisper-cli", $"Whisper CLI ready: {cli}");
    }

    internal static (ProcessStartInfo Psi, string ExpectedTxtPath) BuildWhisperCliCommand(
        string cliPath,
        string wavPath,
        string language,
        string modelPath,
        string outDir,
        IReadOnlyDictionary<string, string?>? envOverrides = null)
    {
        if (IsDisallowedScriptWrapper(cliPath))
            throw new InvalidOperationException("Batch script wrappers (.bat / .cmd) cannot be used as the Whisper CLI binary.");

        var flavor = ClassifyWhisperCli(cliPath);
        var langIso2 = ToIso2Language(language);
        var modelArg = string.IsNullOrWhiteSpace(modelPath) ? "" : ExpandPath(modelPath);

        var psi = new ProcessStartInfo(cliPath) { WorkingDirectory = AppPaths.NeutralCwd };
        SpeechProcessEnv.ConfigureMinimal(psi, envOverrides);

        string expectedTxt;
        switch (flavor)
        {
            case WhisperCliFlavor.MlxWhisper:
                psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("txt");
                psi.ArgumentList.Add("--output-dir"); psi.ArgumentList.Add(outDir);
                if (!string.IsNullOrWhiteSpace(modelArg)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelArg); }
                if (!string.IsNullOrWhiteSpace(langIso2)) { psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(langIso2); }
                expectedTxt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wavPath) + ".txt");
                break;

            case WhisperCliFlavor.PythonWhisper:
                psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("--output_format"); psi.ArgumentList.Add("txt");
                psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
                if (!string.IsNullOrWhiteSpace(modelArg)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelArg); }
                if (!string.IsNullOrWhiteSpace(langIso2)) { psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(langIso2); }
                expectedTxt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wavPath) + ".txt");
                break;

            default:
                if (string.IsNullOrWhiteSpace(modelArg))
                    throw new InvalidOperationException("whisper.cpp requires a local GGML model file in Settings (Whisper model path or repo).");

                var outBase = Path.Combine(outDir, "transcript");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelArg);
                psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(langIso2 ?? "auto");
                psi.ArgumentList.Add("-nt");
                psi.ArgumentList.Add("-otxt");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
                expectedTxt = outBase + ".txt";
                break;
        }

        return (psi, expectedTxt);
    }

    private async Task<string> TranscribeWhisperCliAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        var (cli, cliErr) = ResolveWhisperCliExecutable(cfg);
        if (cli is null)
            throw new InvalidOperationException(cliErr ?? "Whisper CLI binary not found (install mlx_whisper, whisper-cli, whisper-cpp, or whisper, or configure its path).");

        var flavor = ClassifyWhisperCli(cli);
        var modelArg = ExpandPath(cfg.WhisperModelPath);
        if (flavor == WhisperCliFlavor.WhisperCpp && (string.IsNullOrWhiteSpace(modelArg) || !File.Exists(modelArg)))
            throw new InvalidOperationException("whisper.cpp requires an existing local GGML model file in 'Whisper model path or repo'.");

        var outDir = Path.Combine(_tempRoot, "wout-" + Guid.NewGuid().ToString("N"));
        SpeechStoragePermissions.EnsurePrivateAgentDir(outDir);

        try
        {
            var (psi, expectedTxt) = BuildWhisperCliCommand(cli, wavPath, language, modelArg, outDir);
            var timeout = ComputeWhisperCliTimeout(cfg.MaxRecordSec);
            var res = await ToolProcess.RunAsync(psi, timeout, ct);
            if (res.ExitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(res.Error) ? res.Output : res.Error;
                throw new InvalidOperationException($"Whisper CLI failed (exit {res.ExitCode}): {err.Trim()}");
            }

            if (File.Exists(expectedTxt))
                return (await File.ReadAllTextAsync(expectedTxt, ct)).Trim();

            return res.Output.Trim();
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }
}
