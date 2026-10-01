using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyTracker.WinGet.Storage;
using Xunit;

namespace TinyTracker.WinGet.Tests.Storage;

// --cleanup --remove-data and the helper's update folder: a folder and all it holds go, and a link goes as a link (spec §6.5, §10).
public sealed class FolderTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));

    public FolderTreeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // Somewhere else, with a file that must stay.
    private string Elsewhere()
    {
        var elsewhere = Folder("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        return elsewhere;
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void Delete_TakesTheFolderAndAllItHolds()
    {
        var data = Folder("Tiny Tracker");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(data, "logs", "app.log"), "log");
        FolderTree.Delete(data);
        Assert.False(Directory.Exists(data));
        FolderTree.Delete(data);
    }

    [Fact]
    public void Delete_TakesReadOnlyFilesToo()
    {
        var data = Folder("Tiny Tracker");
        var file = Path.Combine(data, "settings.json");
        File.WriteAllText(file, "{}");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            FolderTree.Delete(data);
        }
        finally
        {
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        }
        Assert.False(Directory.Exists(data));
    }

    // A file it may delete but not read goes too.
    [Fact]
    public void Delete_TakesAFileItMayNotRead()
    {
        var data = Folder("Tiny Tracker");
        var file = new FileInfo(Path.Combine(data, "settings.json"));
        File.WriteAllText(file.FullName, "{}");
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = file.GetAccessControl();
        security.AddAccessRule(deny);
        file.SetAccessControl(security);
        try
        {
            FolderTree.Delete(data);
        }
        finally
        {
            if (file.Exists)
            {
                security.RemoveAccessRule(deny);
                file.SetAccessControl(security);
            }
        }
        Assert.False(Directory.Exists(data));
    }

    // Another program, such as a virus scanner, may hold a file for a moment: a later pass takes it.
    [Fact]
    public async Task FileHeldForAMoment_GoesOnALaterPass()
    {
        var data = Folder("Tiny Tracker");
        var file = Path.Combine(data, "history.json");
        File.WriteAllText(file, "{}");
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            held.Dispose();
        }, TestContext.Current.CancellationToken);
        FolderTree.Delete(data);
        await letGo;
        Assert.False(Directory.Exists(data));
    }

    // One held throughout stays, and so does its folder, with the rest gone.
    [Fact]
    public void FileHeldThroughout_FailsTheDelete()
    {
        var data = Folder("Tiny Tracker");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        var file = Path.Combine(data, "history.json");
        File.WriteAllText(file, "{}");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => FolderTree.Delete(data));
        Assert.Equal([file], Directory.GetFileSystemEntries(data));
    }

    // A folder made to point somewhere else loses only the link.
    [Fact]
    public void Delete_OfALink_LeavesWhatItPointsTo()
    {
        var elsewhere = Elsewhere();
        var data = Path.Combine(_root, "Tiny Tracker");
        Junction(data, elsewhere);
        FolderTree.Delete(data);
        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    // A link inside goes too, and what it points to stays.
    [Fact]
    public void Delete_OfAFolderWithALinkInside_LeavesWhatTheLinkPointsTo()
    {
        var elsewhere = Elsewhere();
        var data = Folder("Tiny Tracker");
        Junction(Path.Combine(data, "logs"), elsewhere);
        FolderTree.Delete(data);
        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    // A symbolic link goes as a link too. Making one takes admin rights or developer mode.
    [Fact]
    public void Delete_OfAFolderWithASymbolicLinkInside_LeavesWhatItPointsTo()
    {
        var elsewhere = Elsewhere();
        var data = Folder("Tiny Tracker");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(data, "logs"), elsewhere);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"No symbolic link here: {e.Message}");
        }
        FolderTree.Delete(data);
        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    // While the delete empties "a", "b" is swapped for a link: it still goes as a link, as the elevated cleanup needs.
    [Fact]
    public void FolderSwappedForALinkMidway_GoesAsALink()
    {
        var elsewhere = Elsewhere();
        var link = Path.Combine(_root, "link");
        Junction(link, elsewhere);
        var data = Folder("Tiny Tracker");
        var slow = Directory.CreateDirectory(Path.Combine(data, "a")).FullName;
        for (var i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(slow, $"{i}.txt"), "");
        var swapped = Directory.CreateDirectory(Path.Combine(data, "b")).FullName;
        File.WriteAllText(Path.Combine(swapped, "b.txt"), "");
        var swaps = 0;
        using var watcher = new FileSystemWatcher(slow) { NotifyFilter = NotifyFilters.FileName };
        watcher.Deleted += (_, _) =>
        {
            if (Interlocked.Exchange(ref swaps, 1) != 0) return;
            try
            {
                Directory.Move(swapped, Path.Combine(_root, "away"));
                Directory.Move(link, swapped);
            }
            catch (IOException)
            {
            }
        };
        watcher.EnableRaisingEvents = true;
        IOException? failed = null;
        try
        {
            FolderTree.Delete(data);
        }
        catch (IOException e)
        {
            failed = e;
        }
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
        // On a busy machine the watcher may hear of the deletes only once "b" is gone.
        Assert.SkipUnless(Directory.Exists(Path.Combine(_root, "away")), "The swap came too late to test.");
        Assert.Null(failed);
        Assert.False(Directory.Exists(data));
    }
}
