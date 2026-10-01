using Microsoft.Win32;
using TinyTracker.Core;
using TinyTracker.Core.Launch;

namespace TinyTracker.App.Interop;

// This user's Run value named after the app, and the mark Task Manager's Startup apps keeps for it.
internal sealed class RegistryStartupValues : IStartupValues
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = AppInfo.Name;

    public string? ReadRun()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    public void WriteRun(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(Name, command, RegistryValueKind.String);
    }

    public void DeleteRun()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
    }

    public byte[]? ReadApproved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(Name) as byte[];
    }

    public void DeleteApproved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        key?.DeleteValue(Name, throwOnMissingValue: false);
    }
}
