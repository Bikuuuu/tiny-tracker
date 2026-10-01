using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Logging;
using TinyTracker.Presentation.Settings;
using TinyTracker.WinGet;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace TinyTracker.App;

// What the Settings page asks of Windows, on the UI thread. owner: the flyout, which the dialogs belong to; prompting keeps it
// open behind them.
internal sealed class Desktop(Action<string> openLink, bool demo, FileLog log, Func<nint> owner, Action<bool> prompting) : IDesktop
{
    public void OpenLink(string url) => openLink(url);

    public void OpenFolder(string path) => _ = OpenFolderAsync(path);

    // Another app may hold the clipboard for a moment, so setting it gets one more try.
    public bool Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                break;
            }
            catch (Exception e) when (attempt < 2)
            {
                log.Warn($"Clipboard busy: 0x{e.HResult:X8}");
                Thread.Sleep(100);
            }
            catch (Exception e)
            {
                log.Warn($"Clipboard not set: 0x{e.HResult:X8}");
                return false;
            }
        }
        try
        {
            // The text stays on the clipboard after the app quits.
            Clipboard.Flush();
        }
        catch (Exception e)
        {
            log.Warn($"Clipboard not flushed: 0x{e.HResult:X8}");
        }
        return true;
    }

    // The demo asks winget nothing.
    public async Task<string?> WinGetVersionAsync(CancellationToken ct)
    {
        if (demo) return null;
        try
        {
            return await WinGetSession.ReadVersionAsync(ct);
        }
        catch (PackageSourceException)
        {
            return null;
        }
    }

    public Task<string?> PickSaveFileAsync(string suggestedName) => PickAsync(async window =>
    {
        var picker = new FileSavePicker(window)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            DefaultFileExtension = ".json",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        return (await picker.PickSaveFileAsync())?.Path;
    });

    public Task<string?> PickOpenFileAsync() => PickAsync(async window =>
    {
        var picker = new FileOpenPicker(window) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        return (await picker.PickSingleFileAsync())?.Path;
    });

    // A dialog takes the focus; the flyout stays open behind it, as behind a UAC prompt.
    private async Task<string?> PickAsync(Func<WindowId, Task<string?>> pick)
    {
        prompting(true);
        try
        {
            return await pick(Win32Interop.GetWindowIdFromWindow(owner()));
        }
        catch (Exception e)
        {
            log.Warn($"File dialog failed: 0x{e.HResult:X8}");
            throw new FileDialogException(e);
        }
        finally
        {
            prompting(false);
        }
    }

    private async Task OpenFolderAsync(string path)
    {
        try
        {
            if (!await Launcher.LaunchFolderPathAsync(path)) log.Warn("Logs folder not opened");
        }
        catch (Exception e)
        {
            log.Warn($"Logs folder not opened: 0x{e.HResult:X8}");
        }
    }
}
