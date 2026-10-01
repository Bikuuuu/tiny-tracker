using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Storage;

// Deletes a folder and all it holds without entering a link, even one swapped in meanwhile (spec §10): each folder is held open,
// and each entry opened through it as the link it may be, checked, and deleted through that handle.
public static class FolderTree
{
    // Entries that keep coming back, or that another program keeps open, are given up on.
    private const int Passes = 3;
    private static readonly TimeSpan HeldWait = TimeSpan.FromMilliseconds(250);
    private const int ListBuffer = 64 * 1024;
    private const uint FileAccess = 0x00010000 | 0x00100000 | 0x0080; // DELETE, SYNCHRONIZE, FILE_READ_ATTRIBUTES
    private const uint FolderAccess = FileAccess | 0x0001; // and FILE_LIST_DIRECTORY
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SynchronousIo = 0x20;
    private const uint DirectoryFile = 0x1;
    private const uint NonDirectoryFile = 0x40;
    private const int NotADirectory = unchecked((int)0xC0000103);
    private const int FileIsADirectory = unchecked((int)0xC00000BA);
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileFullDirectoryInfo = 14;
    private const int FileFullDirectoryRestartInfo = 15;
    private const int FileDispositionInfoEx = 21;
    private const uint DeleteNow = 0x1 | 0x2 | 0x10; // DELETE, POSIX_SEMANTICS, IGNORE_READONLY_ATTRIBUTE
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
    private const uint NameSurrogate = 0x20000000;
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const int NoMoreFiles = 18;
    private const int SharingViolation = 32;
    private const int NotSupported = 50;
    private const int InvalidParameter = 87;

    // A link, the folder itself or one inside, goes as a link, and what it points to stays. Nothing there is fine. Throws
    // IOException when something stays.
    public static void Delete(string root)
    {
        using var folder = CreateFile(root, FolderAccess, ShareReadWrite, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (folder.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is FileNotFound or PathNotFound) return;
            throw Failed(root, error);
        }
        Delete(folder, root);
    }

    private static void Delete(SafeFileHandle entry, string path)
    {
        if (IsFolder(entry, path))
        {
            for (var pass = 1; pass <= Passes; pass++)
            {
                var names = Names(entry, path);
                if (names.Count == 0) break;
                var held = false;
                foreach (var name in names)
                {
                    using var child = Open(entry, name, Path.Combine(path, name), pass == Passes, ref held);
                    if (child is not null) Delete(child, Path.Combine(path, name));
                }
                if (held) Thread.Sleep(HeldWait);
            }
        }
        Remove(entry, path);
    }

    // A folder itself, not a link to one. A reparse point that isn't a link, such as a OneDrive placeholder, is what it holds.
    private static bool IsFolder(SafeFileHandle entry, string path)
    {
        if (!GetFileInformationByHandleEx(entry, FileAttributeTagInfo, out AttributeTagInfo info, Marshal.SizeOf<AttributeTagInfo>()))
            throw Failed(path, Marshal.GetLastPInvokeError());
        var link = (info.Attributes & ReparsePoint) != 0 && (info.ReparseTag & NameSurrogate) != 0;
        return (info.Attributes & Directory) != 0 && !link;
    }

    // What the held folder holds now, by name.
    private static List<string> Names(SafeFileHandle folder, string path)
    {
        var names = new List<string>();
        var buffer = Marshal.AllocHGlobal(ListBuffer);
        try
        {
            for (var infoClass = FileFullDirectoryRestartInfo; ; infoClass = FileFullDirectoryInfo)
            {
                if (!GetFileInformationByHandleEx(folder, infoClass, buffer, ListBuffer))
                {
                    var error = Marshal.GetLastPInvokeError();
                    return error == NoMoreFiles ? names : throw Failed(path, error);
                }
                // FILE_FULL_DIR_INFO: the next entry's offset first, the name's length in bytes at 60, and the name at 68.
                for (var offset = 0; ; )
                {
                    var name = Marshal.PtrToStringUni(buffer + offset + 68, Marshal.ReadInt32(buffer, offset + 60) / 2);
                    if (name is not ("." or "..")) names.Add(name);
                    var next = Marshal.ReadInt32(buffer, offset);
                    if (next == 0) break;
                    offset += next;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The entry in the held folder, a link as itself, which others may read and write but not move; a folder opens to list,
    // anything else without read. Null once gone, or, before the last pass, while another program holds it (held says so).
    private static SafeFileHandle? Open(SafeFileHandle folder, string name, string path, bool lastPass, ref bool held)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            folder.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text }, unicode, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = folder.DangerousGetHandle(), ObjectName = unicode };
            var status = NtOpenFile(out var entry, FolderAccess, ref attributes, out _, ShareReadWrite, SynchronousIo | OpenReparsePoint | DirectoryFile);
            if (status == NotADirectory)
            {
                entry.Dispose();
                status = NtOpenFile(out entry, FileAccess, ref attributes, out _, ShareReadWrite, SynchronousIo | OpenReparsePoint | NonDirectoryFile);
            }
            if (status >= 0) return entry;
            entry.Dispose();
            // Swapped for a folder between the two looks: the next pass takes it.
            if (status == FileIsADirectory) return null;
            var error = RtlNtStatusToDosError(status);
            if (error is FileNotFound or PathNotFound) return null;
            if (error != SharingViolation || lastPass) throw Failed(path, error);
            held = true;
            return null;
        }
        finally
        {
            if (added) folder.DangerousRelease();
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(text);
        }
    }

    // POSIX semantics take the name at once, read-only or not. A file system without them takes it once the handle closes, which
    // comes before its folder goes.
    private static void Remove(SafeFileHandle entry, string path)
    {
        var flags = DeleteNow;
        if (SetFileInformationByHandle(entry, FileDispositionInfoEx, ref flags, sizeof(uint))) return;
        var error = Marshal.GetLastPInvokeError();
        if (error is InvalidParameter or NotSupported)
        {
            byte delete = 1;
            if (SetFileInformationByHandle(entry, FileDispositionInfo, ref delete, sizeof(byte))) return;
            error = Marshal.GetLastPInvokeError();
        }
        throw Failed(path, error);
    }

    private static IOException Failed(string path, int error) =>
        new($"{path} wasn't deleted: {new Win32Exception(error).Message}", unchecked((int)0x80070000 | (error & 0xFFFF)));
}
