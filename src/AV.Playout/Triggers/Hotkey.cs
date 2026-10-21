using System.Windows.Input;

namespace AV.Playout.Triggers;

/// <summary>A key plus modifiers, e.g. F13 or Ctrl+NumPad1.</summary>
public readonly record struct Hotkey(Key Key, ModifierKeys Modifiers)
{
    public override string ToString()
    {
        var prefix = "";
        if (Modifiers.HasFlag(ModifierKeys.Control)) prefix += "Ctrl+";
        if (Modifiers.HasFlag(ModifierKeys.Alt)) prefix += "Alt+";
        if (Modifiers.HasFlag(ModifierKeys.Shift)) prefix += "Shift+";
        return prefix + KeyName(Key);
    }

    /// <summary>Round-trips <see cref="ToString"/> for the show file.</summary>
    public string Serialize() => $"{Modifiers}|{Key}";

    public static Hotkey? Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('|');
        return parts.Length == 2
               && Enum.TryParse(parts[0], out ModifierKeys mods)
               && Enum.TryParse(parts[1], out Key key)
            ? new Hotkey(key, mods)
            : null;
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        Key.Add => "Num+",
        Key.Subtract => "Num−",
        Key.Multiply => "Num*",
        Key.Divide => "Num/",
        Key.Return => "Enter",
        _ => key.ToString(),
    };
}
