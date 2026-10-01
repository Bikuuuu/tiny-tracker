using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TinyTracker.WinGet.Interop;

// The Windows calls about processes, their tokens, handles, files, connections, windows and COM objects, in one place.
internal static class NativeMethods
{
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenDuplicate = 0x0002;
    public const uint TokenQuery = 0x0008;
    public const uint CreateSuspended = 0x4;
    public const uint CreateUnicodeEnvironment = 0x400;
    public const uint ExtendedStartupInfoPresent = 0x80000;
    public const uint CreateNoWindow = 0x08000000;
    public const int StartfUseStdHandles = 0x100;
    public const uint HandleFlagInherit = 0x1;
    public const nint ProcThreadAttributeHandleList = 0x20002;
    public const uint StillActive = 259;
    public const uint Synchronize = 0x00100000;
    private const uint AnyServer = 0x15;

    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    // The program of a process, in full.
    public static string? PathOf(SafeProcessHandle process)
    {
        var buffer = new char[32768];
        var size = buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    // A COM object, called through its source-generated interface T.
    public static T CreateComObject<T>(Guid type) where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(type, 0, AnyServer, typeof(T).GUID, out var pointer));
        try
        {
            return (T)s_comWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    // A VARIANT for those calls: empty, a number, or a string it holds until disposed. ComVariant would need runtime marshalling
    // turned off for the whole assembly.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct Variant : IDisposable
    {
        private const ushort Number = 3, Text = 8;
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private nint _value;

        public Variant(int value) => (_type, _value) = (Number, value);

        public Variant(string value) => (_type, _value) = (Text, Marshal.StringToBSTR(value));

        public void Dispose()
        {
            if (_type == Text) Marshal.FreeBSTR(_value);
            _type = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public nint DefaultHeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int CountX;
        public int CountY;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    // RM_UNIQUE_PROCESS: the id and the start time, a FILETIME aligned to 4 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UniqueProcess
    {
        public int ProcessId;
        public long StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public string? Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    public static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    public static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    public static extern bool CreateProcess(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    public static extern bool CreateProcess(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResumeThread(nint thread);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFamilyName(SafeProcessHandle process, ref int length, char[]? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFullName(SafeProcessHandle process, ref int length, char[]? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagePathByFullName(string fullName, ref int length, char[]? path);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryUnbiasedInterruptTime(out ulong time);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, nint buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenMutexW")]
    public static extern nint OpenMutex(uint access, bool inherit, string name);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint buffer, int length, out int returned);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("iphlpapi.dll")]
    public static extern int GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, int reserved);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, nint buffer, int length, out int returned);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, out nint value, int length, out int returned);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, [Out] nint[] buffer, int length, out int returned);

    [DllImport("ntdll.dll")]
    public static extern int NtOpenFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, uint share, uint options);

    [DllImport("ntdll.dll")]
    public static extern int RtlNtStatusToDosError(int status);

    public const uint WmClose = 0x0010;

    public delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(nint window, char[] name, int length);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint session, int flags, char[] key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(uint session, uint fileCount, string[]? files, uint processCount, UniqueProcess[] processes, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmShutdown(uint session, uint flags, nint callback);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmCancelCurrentTask(uint session);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint session);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    public static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteExW")]
    public static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid type, nint outer, uint context, in Guid id, out nint instance);
}
