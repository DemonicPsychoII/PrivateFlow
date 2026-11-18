namespace PrivateFlow.Core;

// What a key types on a keyboard layout; the Windows project asks the active layout, the checks pass a fixed one.
public interface IKeyboardLayout
{
    // Empty when the key types nothing with these modifiers.
    string Typed(uint virtualKey, bool shift, bool altGr);
}

// Windows RegisterHotKey modifier values; validation stays independent of the desktop UI.
public readonly record struct ActivationShortcut(uint Modifiers, uint Key)
{
    public static ActivationShortcut Default => new(3, 0x20); // Ctrl + Alt + Space
    public bool IsValid => Problem is null;

    // Why a pressed combination cannot be a shortcut, said in the shortcut field; null for a valid one.
    public string? Problem =>
        Key is 0x1b or 0x09 or 0x0d ? "Escape, Tab and Enter are reserved; Escape cancels a dictation."
        : (Modifiers & ~15u) != 0 || Key is < 0x20 or >= 255 || Key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or 0x5d || Key is >= 0xa0 and <= 0xa5
            ? "That key cannot be used in a shortcut."
        : (Modifiers & 11) == 0 && Key is not (>= 0x70 and <= 0x87) ? "Add Ctrl, Alt or Win, or use a function key; Shift alone is for typing."
        : (Modifiers & 3) == 3 && Key == 0x2e ? "Windows reserves Ctrl + Alt + Delete for its security screen."
        : (Modifiers & 8) != 0 && Key == 0x4c ? "Windows reserves Win + L to lock the PC."
        : Modifiers == 1 && Key == 0x73 ? "Alt + F4 closes the window in front; it cannot be a shortcut."
        : null;

    // Windows takes AltGr as Ctrl + Alt, so a Ctrl + Alt shortcut without Win also catches AltGr with its key, in every program.
    // The character that AltGr combination types on the layout, which cannot be typed while the shortcut is registered; null when
    // it types nothing, or nothing the key does not type without AltGr anyway.
    public string? AltGrCharacter(IKeyboardLayout layout)
    {
        if ((Modifiers & 11) != 3) return null;
        var shift = (Modifiers & 4) != 0;
        var typed = layout.Typed(Key, shift, true);
        if (typed.All(character => char.IsControl(character) || char.IsWhiteSpace(character))) return null;
        return typed == layout.Typed(Key, shift, false) ? null : typed;
    }
    public static string AltGrWarning(string shortcutName, string altGrKeyName, string character) =>
        $"{shortcutName} also is AltGr + {altGrKeyName}, which types {character} on this keyboard; while Private Flow runs, that character cannot be typed with it.";
}
