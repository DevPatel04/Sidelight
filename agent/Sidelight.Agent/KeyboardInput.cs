namespace Sidelight.Agent;

/// <summary>
/// Keyboard widget: a full on-screen keyboard on the phone whose keys are pressed in the app in front on the computer.
/// Off unless the widget's own "Type on the computer" option is on, on an enabled page of this phone. A tap sends either
/// printable text (a letter, digit or symbol, typed as text so the computer's own layout doesn't change it) or one key
/// combination ("ctrl+c", "shift+tab", "f5"). Several combos at once ("ctrl+a, delete") are refused: the phone sends one
/// tap at a time, and a list would let one request run a whole macro. Nothing pressed is logged.
/// </summary>
public sealed class KeyboardInput(ConfigStore config, ILogger<KeyboardInput> log)
{
    public const string WidgetType = "keyboard";
    /// <summary>The most text one tap sends: a key is one character, a held key repeats a few at once.</summary>
    public const int MaxText = 32;

    internal Func<string, CancellationToken, bool> TypeText { get; set; } = KeySender.TryTypeText;
    internal Func<List<string>, bool> SendCombo { get; set; } = KeySender.SendCombo;
    internal Func<FocusedApp?> Focused { get; set; } = AppConsole.FocusedNow;
    internal bool CheckElevation { get; set; } = OperatingSystem.IsWindows();
    internal bool AgentElevated { get; set; } = Environment.IsPrivilegedProcess;

    // keys must arrive in the order they were tapped, and the phone may send the next before this one is done
    private readonly SemaphoreSlim _pressing = new(1, 1);

    public async Task<(int Status, string? Error)> PressAsync(string? deviceId, string? widget, string? key, string? text, CancellationToken ct)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType)?.Bool("control") != true)
            return (403, "typing is off: turn on \"Type on the computer\" in the keyboard widget's settings");

        List<string>? combo = null;
        string? typed = null;
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length > 64) return (400, "unknown key");
            try
            {
                var combos = KeySender.Parse(key);
                if (combos.Count != 1) return (400, "one key at a time");
                combo = combos[0];
            }
            catch (ArgumentException ex) { return (400, ex.Message); }
        }
        else
        {
            // control characters would be Enter or Tab in disguise: those come as keys, never as text
            if ((text ?? "").Length > MaxText) return (413, "the text is too long");
            typed = new string((text ?? "").Where(ch => !char.IsControl(ch) && ch is not '\u2028' and not '\u2029').ToArray());
            if (typed.Length == 0) return (400, "nothing to type");
        }
        // Windows drops input to an app running as administrator without a word (UIPI): say so instead
        if (CheckElevation && Focused() is { Elevated: true } && !AgentElevated)
            return (409, "the app in front runs as administrator, so Windows doesn't let the agent type into it");

        if (!await _pressing.WaitAsync(TimeSpan.FromSeconds(2), ct)) return (429, "still pressing the previous key");
        try
        {
            ct.ThrowIfCancellationRequested();
            var ok = combo is not null ? SendCombo(combo) : TypeText(typed!, ct);
            return ok ? (200, null) : (500, "the computer refused the key");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("keyboard: pressing failed ({Type})", ex.GetType().Name); // never the key
            return (500, "the key could not be pressed");
        }
        finally { _pressing.Release(); }
    }
}
