using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TinyTracker.Core.Installing;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Closing;

// One running process: its program, its own command line and working folder, when it started (which makes its id unique), and
// what started it. ParentPath is null once that process is gone. Console: the program is a console one.
public sealed record RunningApp(int Id, int ParentId, string Path, string CommandLine, long StartTime, string? Directory, string? ParentPath, bool Console);

// A process found in an app's folder, and a handle to it, which keeps its id from going to another process until it's disposed.
internal sealed class OpenApp(RunningApp app, SafeProcessHandle handle, bool canEnd) : IDisposable
{
    public RunningApp App => app;

    // False when it runs as admin, or Windows won't let it be ended from here.
    public bool CanEnd => canEnd;

    public bool Exited => RunningApps.Exited(handle);

    public bool End() => canEnd && RunningApps.End(handle);

    public void Dispose() => handle.Dispose();
}

// This user's processes, as Close & update sees them.
public static class RunningApps
{
    private const uint SnapProcess = 0x2;
    private const int BasicInformation = 0;
    private const int Wow64Information = 26;
    private const int CommandLineInformation = 60;
    private const int TokenUser = 1;
    private const ushort ConsoleSubsystem = 3;

    // This user's processes in this session whose program is in the folder, except those in the folders of other apps installed
    // inside it. Never this process itself.
    public static List<RunningApp> In(string folder, IReadOnlyCollection<string>? othersInside = null)
    {
        var open = Open(folder, othersInside ?? []);
        foreach (var app in open) app.Dispose();
        return [.. open.Select(a => a.App)];
    }

    // Every process of that program in this session, whoever runs it. Never this process itself.
    public static List<RunningApp> Of(string program)
    {
        var open = OfProgram(program);
        foreach (var app in open) app.Dispose();
        return [.. open.Select(a => a.App)];
    }

    // The sessions that program runs in, whoever runs it.
    public static List<uint> SessionsOf(string program)
    {
        var sessions = new List<uint>();
        foreach (var (id, _) in Snapshot())
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
            if (!process.IsInvalid && !Exited(process) && string.Equals(PathOf(process), program, StringComparison.OrdinalIgnoreCase) && SessionOf(id) is { } session)
                sessions.Add(session);
        }
        return sessions;
    }

    // Whether a program in the folder runs, whoever runs it and in whichever session.
    public static bool AnyIn(string folder)
    {
        foreach (var (id, _) in Snapshot())
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
            if (!process.IsInvalid && !Exited(process) && PathOf(process) is { } path && AppFolders.Holds(folder, path)) return true;
        }
        return false;
    }

    public static uint? OwnSession => SessionOf(Environment.ProcessId);

    // The ones that another of them didn't start: started again, they start the rest themselves.
    public static List<RunningApp> Roots(IReadOnlyList<RunningApp> apps) => [.. apps.Where(a => !apps.Any(p => p.Id == a.ParentId && p.StartTime <= a.StartTime))];

    // A console program's header says so; a missing or unreadable file doesn't count as one.
    public static bool IsConsole(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            file.Position = 0x3C;
            // After "PE\0\0" and the 20-byte file header, the optional header has the subsystem at 68.
            file.Position = reader.ReadInt32() + 4 + 20 + 68;
            return reader.ReadUInt16() == ConsoleSubsystem;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static List<OpenApp> Open(string folder, IReadOnlyCollection<string> othersInside)
    {
        using var me = WindowsIdentity.GetCurrent();
        return Open((path, query) => AppFolders.Holds(folder, path) && !othersInside.Any(o => AppFolders.Holds(o, path)) && OwnerOf(query) == me.User);
    }

    // The uninstaller's: every process of that program in this session, whoever runs it.
    internal static List<OpenApp> OfProgram(string program) => Open((path, _) => string.Equals(path, program, StringComparison.OrdinalIgnoreCase));

    // The processes in this session that keep says to, never this one.
    private static List<OpenApp> Open(Func<string, SafeProcessHandle, bool> keep)
    {
        var own = Environment.ProcessId;
        var session = SessionOf(own);
        var found = new List<OpenApp>();
        foreach (var (id, parent) in Snapshot())
        {
            if (id == own || id == 0 || SessionOf(id) != session) continue;
            var query = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id);
            if (query.IsInvalid || Exited(query) || PathOf(query) is not { } path || !GetProcessTimes(query, out var started, out _, out _, out _) || !keep(path, query))
            {
                query.Dispose();
                continue;
            }
            // The query handle keeps the id meanwhile, so this is the same process.
            var end = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, (uint)id);
            var canEnd = !end.IsInvalid;
            if (canEnd) query.Dispose();
            else end.Dispose();
            var app = new RunningApp(id, parent, path, CommandLineOf(canEnd ? end : query) ?? $"\"{path}\"", started, DirectoryOf(id), ParentPathOf(parent, started), IsConsole(path));
            found.Add(new OpenApp(app, canEnd ? end : query, canEnd));
        }
        return found;
    }

    // A process whose exit code says it's still active counts as running, and so does one Windows won't tell about.
    internal static bool Exited(SafeProcessHandle process) => GetExitCodeProcess(process, out var code) && code != StillActive;

    internal static bool End(SafeProcessHandle process) => TerminateProcess(process, 1);

    private static uint? SessionOf(int id) => ProcessIdToSessionId((uint)id, out var session) ? session : null;

    // Every process now, with the one that started it.
    internal static List<(int Id, int Parent)> Snapshot()
    {
        var processes = new List<(int, int)>();
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid) return processes;
        var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
        for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            processes.Add((entry.ProcessId, entry.ParentProcessId));
        return processes;
    }

    // The program of the process that started this one; null when that one is gone and its id went to a newer process.
    private static string? ParentPathOf(int parent, long childStarted)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)parent);
        if (process.IsInvalid || !GetProcessTimes(process, out var started, out _, out _, out _) || started > childStarted) return null;
        return PathOf(process);
    }

    private static SecurityIdentifier? OwnerOf(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, TokenUser, 0, 0, out var needed);
            if (needed <= 0) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                // TOKEN_USER starts with a pointer to the SID.
                return GetTokenInformation(token, TokenUser, buffer, needed, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // The process's own command line, as it was started.
    private static string? CommandLineOf(SafeProcessHandle process)
    {
        NtQueryInformationProcess(process, CommandLineInformation, 0, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(process, CommandLineInformation, buffer, needed, out _) != 0) return null;
            // A UNICODE_STRING: its length in bytes, then a pointer to the text after it.
            var length = (ushort)Marshal.ReadInt16(buffer);
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The process's working folder, from its process parameters. Null when Windows won't let them be read, as for one that runs
    // as admin, or when they don't look right.
    private static string? DirectoryOf(int id)
    {
        using var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)id);
        if (process.IsInvalid) return null;
        try
        {
            if (!IsWow64Process(process, out var wow64)) return null;
            if (wow64)
            {
                // 32-bit: its own PEB points to its parameters, whose current folder is a 32-bit UNICODE_STRING at 0x24.
                if (NtQueryInformationProcess(process, Wow64Information, out nint peb32, IntPtr.Size, out _) != 0 || peb32 == 0) return null;
                var parameters32 = (nint)Read(process, peb32 + 0x10, 4);
                return Text(process, (nint)Read(process, parameters32 + 0x28, 4), (int)Read(process, parameters32 + 0x24, 2));
            }
            var basic = new nint[6];
            if (NtQueryInformationProcess(process, BasicInformation, basic, basic.Length * IntPtr.Size, out _) != 0) return null;
            // The PEB points to the parameters at 0x20; their current folder is a UNICODE_STRING at 0x38.
            var parameters = (nint)Read(process, basic[1] + 0x20, IntPtr.Size);
            return Text(process, (nint)Read(process, parameters + 0x40, IntPtr.Size), (int)Read(process, parameters + 0x38, 2));
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    // A little-endian number of the given size.
    private static long Read(SafeProcessHandle process, nint address, int size)
    {
        var buffer = new byte[8];
        if (!ReadProcessMemory(process, address, buffer, size, out var read) || read != size) throw new Win32Exception();
        return BitConverter.ToInt64(buffer);
    }

    private static string? Text(SafeProcessHandle process, nint address, int bytes)
    {
        if (address == 0 || bytes <= 0 || bytes % 2 != 0) return null;
        var buffer = new byte[bytes];
        if (!ReadProcessMemory(process, address, buffer, bytes, out var read) || read != bytes) return null;
        var folder = System.Text.Encoding.Unicode.GetString(buffer);
        return Path.IsPathFullyQualified(folder) ? folder : null;
    }
}
