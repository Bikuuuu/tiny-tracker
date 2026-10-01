namespace TinyTracker.Presentation.Shell;

public enum ToastAction
{
    View,
    UpdateAll,
    Install,
    CloseAndUpdate,
    // Windows dismisses the toast, and the app does nothing.
    Later,
    // Tiny Tracker's own update, and its release page.
    SelfUpdate,
    WhatsNew,
}

// What a toast's click tells the app. Windows' own dismiss still reaches it from the notification center, as Later.
public static class ToastArguments
{
    public const string Dismiss = "dismiss";

    public static string Of(ToastAction action) => action switch
    {
        ToastAction.UpdateAll => "updateAll",
        ToastAction.Install => "install",
        ToastAction.CloseAndUpdate => "closeAndUpdate",
        ToastAction.Later => Dismiss,
        ToastAction.SelfUpdate => "selfUpdate",
        ToastAction.WhatsNew => "whatsNew",
        _ => "view",
    };

    public static ToastAction ActionOf(string? argument) => argument switch
    {
        "updateAll" => ToastAction.UpdateAll,
        "install" => ToastAction.Install,
        "closeAndUpdate" => ToastAction.CloseAndUpdate,
        Dismiss => ToastAction.Later,
        "selfUpdate" => ToastAction.SelfUpdate,
        "whatsNew" => ToastAction.WhatsNew,
        _ => ToastAction.View,
    };
}

public sealed record ToastButton(string Text, ToastAction Action);

// A Windows toast: a title, one more line and its buttons. Clicking the body is View.
// A toast of the same kind replaces the one before it.
public sealed record Toast(string Kind, string Title, string Body, IReadOnlyList<ToastButton> Buttons);

// Shows toasts under the app's own name and icon. The App does it through Windows.
public interface IToasts
{
    void Show(Toast toast);

    // Takes a kind's toast out of the notification center.
    void Hide(string kind);
}
