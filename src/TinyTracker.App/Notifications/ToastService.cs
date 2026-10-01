using System.Security;
using Microsoft.Win32;
using TinyTracker.Core;
using TinyTracker.Core.Logging;
using TinyTracker.Presentation.Shell;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TinyTracker.App.Notifications;

// Windows.UI.Notifications, as AppNotificationManager can't register in self-contained apps (WindowsAppSDK #6774). Clicks reach
// only the toast's process, so toasts go at start and quit, and Windows drops them at restart. The demo has its own name.
internal sealed class ToastService(bool demo, FileLog log) : IToasts
{
    private const string KeyRoot = @"Software\Classes\AppUserModelId\";
    private const string DemoAumid = AppInfo.InstanceKey + ".Demo";

    private readonly string _aumid = demo ? DemoAumid : AppInfo.InstanceKey;
    // Keeps each kind's latest toast, and so its click handler, alive.
    private readonly Dictionary<string, ToastNotification> _shown = [];

    // Raised on a worker thread when a toast's body or a button other than Later is clicked.
    public event EventHandler<ToastAction>? Activated;

    public string? Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyRoot + _aumid);
            key.SetValue("DisplayName", demo ? AppInfo.Name + " Demo" : AppInfo.Name);
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "app-64.png"));
            return null;
        }
        catch (Exception ex)
        {
            return $"0x{ex.HResult:X8} {ex.Message}";
        }
    }

    public void Show(Toast toast)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(Xml(toast));
            var notification = new ToastNotification(xml) { Tag = toast.Kind, Group = AppInfo.InstanceKey, ExpiresOnReboot = true };
            notification.Activated += (_, args) =>
            {
                var action = ToastArguments.ActionOf((args as ToastActivatedEventArgs)?.Arguments);
                if (action != ToastAction.Later) Activated?.Invoke(this, action);
            };
            lock (_shown) _shown[toast.Kind] = notification;
            ToastNotificationManager.CreateToastNotifier(_aumid).Show(notification);
        }
        catch (Exception e)
        {
            log.Warn($"Toast not shown: 0x{e.HResult:X8}");
        }
    }

    public void Hide(string kind)
    {
        lock (_shown) _shown.Remove(kind);
        try { ToastNotificationManager.History.Remove(kind, AppInfo.InstanceKey, _aumid); } catch (Exception) { }
    }

    public void ClearHistory()
    {
        try { ToastNotificationManager.History.Clear(_aumid); } catch (Exception) { }
    }

    // The demo leaves nothing behind.
    public void RemoveDemo()
    {
        if (demo) Remove(DemoAumid);
    }

    // For --cleanup: the app's registration and a demo's that a crash left.
    public static void RemoveRegistration()
    {
        Remove(AppInfo.InstanceKey);
        Remove(DemoAumid);
    }

    private static void Remove(string aumid)
    {
        try { ToastNotificationManager.History.Clear(aumid); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(KeyRoot + aumid, throwOnMissingSubKey: false);
    }

    // Later is Windows' own dismiss.
    private static string Xml(Toast toast)
    {
        var buttons = string.Concat(toast.Buttons.Select(b => b.Action == ToastAction.Later
            ? $"<action content=\"{Escape(b.Text)}\" arguments=\"{ToastArguments.Dismiss}\" activationType=\"system\"/>"
            : $"<action content=\"{Escape(b.Text)}\" arguments=\"{ToastArguments.Of(b.Action)}\" activationType=\"foreground\"/>"));
        var body = toast.Body.Length > 0 ? $"<text>{Escape(toast.Body)}</text>" : "";
        return $"<toast launch=\"{ToastArguments.Of(ToastAction.View)}\" activationType=\"foreground\"><visual><binding template=\"ToastGeneric\">"
            + $"<text>{Escape(toast.Title)}</text>{body}</binding></visual>"
            + (buttons.Length > 0 ? $"<actions>{buttons}</actions>" : "") + "</toast>";
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

}
