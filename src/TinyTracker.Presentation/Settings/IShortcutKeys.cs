using TinyTracker.Core.Settings;

namespace TinyTracker.Presentation.Settings;

// Why a shortcut doesn't work (spec §4.8).
public enum ShortcutProblem
{
    None,
    // Another app owns the combination.
    InUse,
    // Windows refused it for another reason; Error has its code.
    Failed,
}

// The global shortcut (spec §4.8). The App registers it with Windows.
public interface IShortcutKeys
{
    // Why the saved shortcut can't work, while it can't.
    ShortcutProblem Problem { get; }

    // Windows' error code for the last shortcut it refused.
    int Error { get; }

    // Uses this shortcut in place of the current one: None once it does, else why not, and the current one then stays.
    // Null clears it.
    ShortcutProblem TryUse(Shortcut? shortcut);

    // The current shortcut stops while a new one is recorded. TryUse ends the pause.
    void Pause();

    // The key's name in the keyboard layout, such as "U" or "F12".
    string KeyName(int key);
}
