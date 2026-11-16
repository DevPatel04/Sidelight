namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private bool IsAppleAutoAvailable() =>
        _appleHelper.IsSupported
        && (_appleHelper is not SpeechAppleHelper || SpeechAppleHelper.IsToolchainOrBinaryAvailable());

    internal (string? Provider, string? Error) ResolveProvider(SpeechConfig cfg, SpeechWidgetSettings w)
    {
        var requested = !string.Equals(w.Provider, "default", StringComparison.Ordinal) ? w.Provider : cfg.Provider;

        if (string.Equals(requested, "apple", StringComparison.Ordinal))
        {
            if (!_appleHelper.IsSupported)
                return (null, "Apple Speech Recognition is only supported on macOS.");
            return ("apple", null);
        }

        if (string.Equals(requested, "auto", StringComparison.Ordinal))
        {
            if (IsAppleAutoAvailable())
                return ("apple", null);
            return (null, "Apple Speech Recognition is not available on this computer.");
        }

        return (null, $"Unsupported provider '{requested}'.");
    }

    public async Task<SpeechTestResult> TestProviderAsync(SpeechConfig? candidate, CancellationToken ct = default)
    {
        var cfg = candidate ?? _store.Get().Speech ?? new SpeechConfig();
        cfg.Normalize();

        var dummyWidget = new SpeechWidgetSettings("Speech", "default", cfg.Language, "toggle", "", "append", false, "default", true);
        var (provider, err) = ResolveProvider(cfg, dummyWidget);
        if (provider is null)
            return new SpeechTestResult(false, cfg.Provider, err ?? "No provider configured.");

        try
        {
            if (provider == "apple")
                return await TestAppleAsync(cfg, ct);

            return new SpeechTestResult(false, provider, $"Unsupported provider '{provider}'.");
        }
        catch (Exception ex)
        {
            return new SpeechTestResult(false, provider, SanitizePhoneError(ex.Message, cfg));
        }
    }

    private async Task<SpeechTestResult> TestAppleAsync(SpeechConfig cfg, CancellationToken ct)
    {
        var st = await _appleHelper.GetStatusAsync(cfg.Language, ct);
        if (!string.IsNullOrWhiteSpace(st.Error))
            return new SpeechTestResult(false, "apple", st.Error);

        if (st.SpeechAuth is "denied" or "restricted" || st.MicAuth is "denied" or "restricted")
        {
            return new SpeechTestResult(
                false,
                "apple",
                $"Apple Speech permissions are {st.SpeechAuth}/{st.MicAuth}. Allow Microphone and Speech Recognition in System Settings > Privacy & Security.");
        }

        if (!st.Available)
            return new SpeechTestResult(false, "apple", $"Apple Speech is currently unavailable (speech auth: {st.SpeechAuth}, mic auth: {st.MicAuth}).");

        if (cfg.AppleOnDevice && !st.OnDeviceSupported)
        {
            var loc = string.IsNullOrWhiteSpace(cfg.Language) ? "system default" : cfg.Language.Trim();
            return new SpeechTestResult(
                false,
                "apple",
                $"On-device Apple Speech model is unavailable for '{loc}'. Download the dictation language in macOS System Settings or turn off 'Require on-device Apple Speech'.");
        }

        var modeNote = cfg.AppleOnDevice ? "on-device" : "cloud-capable";
        return new SpeechTestResult(true, "apple", $"Apple Speech ready ({modeNote}; speech: {st.SpeechAuth}, mic: {st.MicAuth}).");
    }

    internal async Task<string> TranscribeWithProviderAsync(
        string provider,
        string wavPath,
        string language,
        SpeechConfig cfg,
        CancellationToken ct)
    {
        if (provider == "apple")
        {
            var (appleText, appleErr) = await _appleHelper.TranscribeAsync(wavPath, language, cfg.AppleOnDevice, ct);
            if (appleErr is not null) throw new InvalidOperationException(appleErr);
            return appleText ?? "";
        }

        throw new InvalidOperationException($"Unsupported speech provider '{provider}'.");
    }
}
