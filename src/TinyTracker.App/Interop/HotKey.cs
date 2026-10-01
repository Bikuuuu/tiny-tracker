using System.Runtime.InteropServices;
using TinyTracker.Core.Settings;
using TinyTracker.Presentation.Settings;

namespace TinyTracker.App.Interop;

// The global shortcut (spec §6.7): RegisterHotKey on the tray icon's hidden window, which then gets WM_HOTKEY.
// Held keys don't repeat it. UI thread only.
internal sealed class HotKey(nint window) : IShortcutKeys, IDisposable
{
    private const int Id = 1;
    private const uint NoRepeat = 0x4000;
    private const uint VkToScanCode = 0;
    // ERROR_HOTKEY_ALREADY_REGISTERED
    private const int Taken = 1409;
    // Keys whose names need the extended-key bit, such as the arrows, Page Up and the menu key.
    private static readonly int[] Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5D, 0x6F, 0x90];

    private Shortcut? _current;
    private Shortcut? _paused;

    public ShortcutProblem Problem { get; private set; }

    public int Error { get; private set; }

    public ShortcutProblem TryUse(Shortcut? shortcut)
    {
        var before = _paused ?? _current;
        _paused = null;
        Unregister();
        if (shortcut is null || Register(shortcut))
        {
            Problem = ShortcutProblem.None;
            return ShortcutProblem.None;
        }
        Error = Marshal.GetLastPInvokeError();
        var problem = Error == Taken ? ShortcutProblem.InUse : ShortcutProblem.Failed;
        if (before is not null) Register(before);
        Problem = _current is null ? problem : ShortcutProblem.None;
        return problem;
    }

    public void Pause()
    {
        if (_current is null) return;
        _paused = _current;
        Unregister();
    }

    public string KeyName(int key)
    {
        // Windows maps Pause to no scan code, and Print Screen to the one named SysRq.
        var (scan, extended) = key switch
        {
            0x13 => (0x45u, false),
            0x2C => (0x37u, true),
            _ => (NativeMethods.MapVirtualKeyW((uint)key, VkToScanCode), Extended.Contains(key)),
        };
        var lParam = (int)(scan << 16) | (extended ? 1 << 24 : 0);
        var name = new char[64];
        var length = scan == 0 ? 0 : NativeMethods.GetKeyNameTextW(lParam, name, name.Length);
        return length > 0 ? new string(name, 0, length) : $"0x{key:X2}";
    }

    public void Dispose() => Unregister();

    private bool Register(Shortcut shortcut)
    {
        if (!NativeMethods.RegisterHotKey(window, Id, (uint)shortcut.Modifiers | NoRepeat, (uint)shortcut.Key)) return false;
        _current = shortcut;
        return true;
    }

    private void Unregister()
    {
        if (_current is null) return;
        NativeMethods.UnregisterHotKey(window, Id);
        _current = null;
    }
}
