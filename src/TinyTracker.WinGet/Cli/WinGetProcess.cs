using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using TinyTracker.Core.Launch;
using TinyTracker.WinGet.Closing;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Cli;

// Who a program started paused is: its exe, and the package it runs as, if any.
public sealed record ProgramIdentity(string? Path, string? PackageFamily, string? PackageFolder);

// winget's command line, running with its output piped here. It starts paused, so it can be checked before it runs a step.
public sealed class WinGetProcess : IDisposable
{
    private const int InsufficientBuffer = 122;
    // Only one program is started from here at a time, so none inherits the pipe meant for another.
    private static readonly Lock Starting = new();
    private readonly SafeProcessHandle _process;
    private readonly long _started;
    private readonly RegisteredWaitHandle _exit;
    private readonly ChannelWriter<string> _lines;
    private nint _thread;

    private WinGetProcess(ProcessInformation started, SafeFileHandle output)
    {
        _process = new SafeProcessHandle(started.Process, ownsHandle: true);
        _thread = started.Thread;
        Id = started.ProcessId;
        GetProcessTimes(_process, out _started, out _, out _, out _);
        Identity = new ProgramIdentity(PathOf(_process), FamilyOf(_process), FolderOf(_process));
        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Lines = lines.Reader;
        _lines = lines.Writer;
        new Thread(() => Read(output, lines.Writer)) { IsBackground = true, Name = "winget output" }.Start();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exit = ThreadPool.RegisterWaitForSingleObject(new ProcessWait(_process), (_, _) =>
        {
            GetExitCodeProcess(_process, out var code);
            exited.TrySetResult(unchecked((int)code));
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        Exited = exited.Task;
    }

    public int Id { get; }

    public ProgramIdentity Identity { get; }

    // Its output, a line at a time; complete once it and whatever inherited its output have ended.
    public ChannelReader<string> Lines { get; }

    // Its exit code, once it ended.
    public Task<int> Exited { get; }

    // Throws Win32Exception when Windows can't start it. It starts in Windows' own folder, never one the user can write.
    internal static WinGetProcess StartPaused(string path, IReadOnlyList<string> arguments)
    {
        if (!CreatePipe(out var read, out var write, 0, 0)) throw new Win32Exception();
        ProcessInformation started;
        using (write)
        {
            var size = (nint)0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            var attributes = Marshal.AllocHGlobal(size);
            var handles = Marshal.AllocHGlobal(IntPtr.Size);
            var listed = false;
            try
            {
                // It inherits its output pipe and nothing else.
                Marshal.WriteIntPtr(handles, write.DangerousGetHandle());
                listed = InitializeProcThreadAttributeList(attributes, 1, 0, ref size);
                if (!listed || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handles, IntPtr.Size, 0, 0))
                    throw new Win32Exception();
                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles,
                        StdOutput = write.DangerousGetHandle(),
                        StdError = write.DangerousGetHandle(),
                    },
                    AttributeList = attributes,
                };
                lock (Starting)
                {
                    SetHandleInformation(write, HandleFlagInherit, HandleFlagInherit);
                    try
                    {
                        if (!CreateProcess(path, new StringBuilder(CommandLine.Join(path, arguments)), 0, 0, true,
                            CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent, 0, Environment.SystemDirectory, ref startup, out started))
                            throw new Win32Exception();
                    }
                    finally
                    {
                        SetHandleInformation(write, HandleFlagInherit, 0);
                    }
                }
            }
            catch
            {
                read.Dispose();
                throw;
            }
            finally
            {
                if (listed) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
                Marshal.FreeHGlobal(handles);
            }
        }
        return new WinGetProcess(started, read);
    }

    // One that couldn't resume would stay paused for good, so it's ended.
    internal void Resume()
    {
        if (ResumeThread(_thread) == -1) End();
        CloseThread();
    }

    // Ends it at once, as Windows does, with nothing it could stop.
    public void End() => TerminateProcess(_process, 1);

    // It started a program of its own, as an installer: not the console host Windows gives it.
    public bool HasChildren()
    {
        var host = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        foreach (var (id, parent) in RunningApps.Snapshot())
        {
            if (parent != Id) continue;
            using var child = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
            // A process started before this one only took over an id it once had.
            if (child.IsInvalid || !GetProcessTimes(child, out var started, out _, out _, out _) || started < _started) continue;
            if (!string.Equals(PathOf(child), host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Once the exit's callback can't run anymore, so it never reads a closed handle. Lines from whatever inherited its output go
    // nowhere then.
    public void Dispose()
    {
        using (var unregistered = new ManualResetEvent(false))
        {
            if (_exit.Unregister(unregistered)) unregistered.WaitOne(TimeSpan.FromSeconds(5));
        }
        _lines.TryComplete();
        CloseThread();
        _process.Dispose();
    }

    private void CloseThread()
    {
        var thread = Interlocked.Exchange(ref _thread, 0);
        if (thread != 0) CloseHandle(thread);
    }

    private static void Read(SafeFileHandle output, ChannelWriter<string> lines)
    {
        try
        {
            using var stream = new FileStream(output, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            while (reader.ReadLine() is { } line) lines.TryWrite(line);
        }
        catch (IOException)
        {
        }
        finally
        {
            lines.TryComplete();
        }
    }

    private static string? FamilyOf(SafeProcessHandle process)
    {
        var length = 0;
        if (GetPackageFamilyName(process, ref length, null) != InsufficientBuffer) return null;
        var name = new char[length];
        return GetPackageFamilyName(process, ref length, name) == 0 ? new string(name, 0, length - 1) : null;
    }

    private static string? FolderOf(SafeProcessHandle process)
    {
        var length = 0;
        if (GetPackageFullName(process, ref length, null) != InsufficientBuffer) return null;
        var name = new char[length];
        if (GetPackageFullName(process, ref length, name) != 0) return null;
        var fullName = new string(name, 0, length - 1);
        length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != InsufficientBuffer) return null;
        var path = new char[length];
        return GetPackagePathByFullName(fullName, ref length, path) == 0 ? new string(path, 0, length - 1) : null;
    }

    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
