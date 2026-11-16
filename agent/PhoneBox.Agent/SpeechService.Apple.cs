namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private bool IsAppleAutoAvailable() =>
        _appleHelper.IsSupported
        && (_appleHelper is not SpeechAppleHelper || SpeechAppleHelper.IsToolchainOrBinaryAvailable());

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

    private async Task<string> TranscribeAppleAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        var (appleText, appleErr) = await _appleHelper.TranscribeAsync(wavPath, language, cfg.AppleOnDevice, ct);
        if (appleErr is not null) throw new InvalidOperationException(appleErr);
        return appleText ?? "";
    }
}
