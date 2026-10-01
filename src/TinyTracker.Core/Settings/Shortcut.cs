namespace TinyTracker.Core.Settings;

// Same values as RegisterHotKey's MOD_* flags.
[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

// A virtual-key code plus modifiers.
public sealed record Shortcut(ShortcutModifiers Modifiers, int Key)
{
    private const ShortcutModifiers Known = ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Windows;

    // Not Ctrl+Alt: that's AltGr on many keyboards, and AltGr+U types a character there.
    public static Shortcut Default { get; } = new(ShortcutModifiers.Windows | ShortcutModifiers.Shift, 0x55);

    // Without Ctrl, Alt or Win, a shortcut would take over normal typing (Shift+A types a capital A).
    public bool IsValid() => Key is >= 1 and <= 254 && (Modifiers & ~Known) == 0
        && (Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Windows)) != 0;
}
