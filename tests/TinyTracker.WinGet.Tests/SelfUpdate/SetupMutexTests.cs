using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TinyTracker.WinGet.SelfUpdate;
using Xunit;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// Whether Tiny Tracker's Setup runs: it holds a mutex while it does.
public sealed class SetupMutexTests
{
    private static string NewName() => @"Local\TinyTracker.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void HeldMutex_SaysSetupRuns()
    {
        var name = NewName();
        using var mutex = new Mutex(false, name);
        Assert.True(SetupMutex.Held(name));
    }

    [Fact]
    public void MutexNobodyHolds_SaysItDoesnt()
    {
        var name = NewName();
        using (new Mutex(false, name))
        {
        }
        Assert.False(SetupMutex.Held(name));
    }

    // One it may not open, as another program's could be, counts as held.
    [Fact]
    public void MutexItMayNotOpen_SaysSetupRuns()
    {
        var name = NewName();
        using var mutex = Denied(name);
        Assert.True(SetupMutex.Held(name));
    }

    // Everyone is denied it, its maker too, past the handle it got.
    private static SafeWaitHandle Denied(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(D;;GA;;;WD)", 1, out var descriptor, 0)) throw new Win32Exception();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateMutexW(ref attributes, false, name);
            return handle.IsInvalid ? throw new Win32Exception() : handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, int revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes attributes, bool initialOwner, string name);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
