using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Icons;
using Windows.Storage.Streams;

namespace TinyTracker.App.Icons;

// App icons from uninstall entries and MSIX packages, loaded into memory only and kept for the session.
// Call it on the UI thread; the file and registry work runs on the thread pool.
internal static class IconLoader
{
    private static readonly Dictionary<string, Task<ImageSource?>> Loaded = [];

    public static Task<ImageSource?> GetAsync(string localId, int pixels)
    {
        var key = $"{pixels}|{localId}";
        if (!Loaded.TryGetValue(key, out var icon)) Loaded[key] = icon = LoadAsync(localId, pixels);
        return icon;
    }

    private static async Task<ImageSource?> LoadAsync(string localId, int pixels)
    {
        try
        {
            switch (LocalApp.Parse(localId))
            {
                case UninstallEntry entry:
                    var bgra = await Task.Run(() => FromUninstallEntry(entry, pixels));
                    if (bgra is null) return null;
                    var bitmap = new WriteableBitmap(pixels, pixels);
                    using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(bgra);
                    bitmap.Invalidate();
                    return bitmap;
                case MsixPackage package:
                    using (var logo = await Task.Run(() => PackageLogoAsync(package.FullName, pixels)))
                    {
                        if (logo is null) return null;
                        var image = new BitmapImage { DecodePixelWidth = pixels, DecodePixelHeight = pixels };
                        await image.SetSourceAsync(logo);
                        return image;
                    }
                default:
                    return null;
            }
        }
        // An icon that can't be read gets a letter tile.
        catch (Exception)
        {
            return null;
        }
    }

    // DisplayIcon first, else the app's program in its install folder.
    private static byte[]? FromUninstallEntry(UninstallEntry entry, int pixels)
    {
        using var root = RegistryKey.OpenBaseKey(entry.PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, entry.Wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = root.OpenSubKey(entry.KeyPath);
        if (key is null) return null;
        if (LocalApp.DisplayIcon(key.GetValue("DisplayIcon") as string) is (var path, var index)
            && File.Exists(path = Environment.ExpandEnvironmentVariables(path))
            && IconPixels.Extract(path, index, pixels) is { } fromIcon)
            return fromIcon;
        if (key.GetValue("InstallLocation") is not string folder || !Directory.Exists(folder = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')))) return null;
        var name = key.GetValue("DisplayName") as string ?? "";
        var program = IconSource.MainProgram(Directory.EnumerateFiles(folder, "*.exe").Select(Path.GetFileName).OfType<string>(), name);
        return program is null ? null : IconPixels.Extract(Path.Combine(folder, program), 0, pixels);
    }

    private static async Task<IRandomAccessStreamWithContentType?> PackageLogoAsync(string fullName, int pixels)
    {
        var package = new Windows.Management.Deployment.PackageManager().FindPackageForUser(string.Empty, fullName);
        return package is null ? null : await package.GetLogoAsRandomAccessStreamReference(new Windows.Foundation.Size(pixels, pixels)).OpenReadAsync();
    }
}
