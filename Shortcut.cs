using System.Windows.Input;

namespace VocalTuner;

/// <summary>A key plus modifiers, stored in settings as text like "Ctrl+Shift+R" or "F9".</summary>
public readonly record struct Shortcut(Key Key, ModifierKeys Mods)
{
    public bool IsEmpty => Key == Key.None;

    public static Shortcut Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        var mods = ModifierKeys.None;
        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            mods |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        return parts.Length > 0 && Enum.TryParse<Key>(parts[^1], true, out var k) ? new Shortcut(k, mods) : default;
    }

    /// <summary>Settings form ("Ctrl+Shift+R"); uses WPF key names so it parses back exactly.</summary>
    public string Serialize() => IsEmpty ? "" : ModText() + Key;

    /// <summary>Human-readable form for the UI.</summary>
    public override string ToString() => IsEmpty ? "" : ModText() + KeyName(Key);

    string ModText() =>
        ((Mods & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
        ((Mods & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
        ((Mods & ModifierKeys.Shift) != 0 ? "Shift+" : "") +
        ((Mods & ModifierKeys.Windows) != 0 ? "Win+" : "");

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
        Key.Space => "Space", Key.Return => "Enter", Key.Back => "Backspace",
        Key.Prior => "Page Up", Key.Next => "Page Down",
        Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.OemQuestion => "/", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\",
        Key.OemMinus => "-", Key.OemPlus => "=", Key.OemTilde => "`",
        Key.Multiply => "Num *", Key.Add => "Num +", Key.Subtract => "Num -", Key.Divide => "Num /", Key.Decimal => "Num .",
        Key.MediaPlayPause => "Play/Pause", Key.MediaStop => "Media Stop",
        Key.MediaNextTrack => "Next Track", Key.MediaPreviousTrack => "Previous Track",
        _ => k.ToString(),
    };

    /// <summary>The real key of a key event (Alt combos and F10 arrive as Key.System).</summary>
    public static Key KeyOf(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;

    /// <summary>A plain letter/digit/space with no Ctrl/Alt/Win: as a global hotkey it would stop that key typing in other apps.</summary>
    public bool BlocksTyping => (Mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0 &&
        (Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Return or Key.Back or Key.Tab
         || Key.ToString().StartsWith("Oem"));
}
