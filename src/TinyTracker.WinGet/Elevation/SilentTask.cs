using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Principal;
using TinyTracker.Core.Installing;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Elevation;

// Silent mode's task (spec §6.6): one per user in \Tiny Tracker, with no triggers, started by the app to run the helper with
// highest privileges while the user is signed in. The helper registers and removes it; the app checks and runs it.
public static partial class SilentTask
{
    public const string Folder = @"\Tiny Tracker";
    // Signed-in users may see the folder; each task keeps its own permissions.
    private const string FolderSecurity = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;AU)";
    private const int LogonInteractiveToken = 3;
    private const int RunLevelHighest = 1;
    private const int InstancesParallel = 0;
    private const int ActionExec = 0;
    private const int CreateOrUpdate = 6;
    private const int EnumHidden = 1;
    private static readonly Guid TaskScheduler = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");

    public static string NameFor(SecurityIdentifier user) => $"Tiny Tracker Helper ({user.Value})";

    // Each run gets a new pipe name as its argument.
    public static string ArgumentsFor(SecurityIdentifier user) => $"--pipe $(Arg0) --user {user.Value}";

    // The user may read and run the task; only SYSTEM and Administrators may change or delete it.
    public static string SecurityFor(SecurityIdentifier user) => $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{user.Value})";

    // Why the helper won't register the task: only from Program Files, as a no-prompt task that runs a file the user can change
    // would give any program admin rights; and only for the user it runs as, who turned silent mode on. Null when it will.
    public static string? WhyNotRegister(string helperPath, SecurityIdentifier runsAs, SecurityIdentifier user)
    {
        if (!AppFolders.Holds(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), helperPath)) return "not in Program Files";
        return runsAs == user ? null : "another user";
    }

    // Run elevated, by the helper. Null once registered, else why not.
    public static string? Register(string helperPath, SecurityIdentifier user) => Try(() =>
    {
        var service = Connect();
        var folder = FolderOf(service) ?? CreateFolder(service);
        var definition = service.NewTask(0);
        var info = definition.get_RegistrationInfo();
        info.put_Author("Tiny Tracker");
        info.put_Description("Installs updates that need admin rights, with no prompt, while Tiny Tracker's silent mode is on.");
        var principal = definition.get_Principal();
        principal.put_UserId(user.Value);
        principal.put_LogonType(LogonInteractiveToken);
        principal.put_RunLevel(RunLevelHighest);
        var settings = definition.get_Settings();
        settings.put_MultipleInstances(InstancesParallel);
        settings.put_DisallowStartIfOnBatteries(false);
        settings.put_StopIfGoingOnBatteries(false);
        settings.put_AllowDemandStart(true);
        settings.put_ExecutionTimeLimit("PT24H");
        var action = definition.get_Actions().Create(ActionExec);
        action.put_Path(helperPath);
        action.put_Arguments(ArgumentsFor(user));
        action.put_WorkingDirectory(Path.GetDirectoryName(helperPath));
        using var userId = new Variant(user.Value);
        using var security = new Variant(SecurityFor(user));
        folder.RegisterTaskDefinition(NameFor(user), definition, CreateOrUpdate, userId, default, LogonInteractiveToken, security);
    });

    // Run elevated, by the helper. The folder goes with the last task in it. Null once removed, or when there was none.
    public static string? Remove(SecurityIdentifier user) => Try(() =>
    {
        var service = Connect();
        var folder = FolderOf(service);
        if (folder is null) return;
        if (TaskOf(folder, user) is not null) folder.DeleteTask(NameFor(user), 0);
        if (folder.GetTasks(EnumHidden).get_Count() == 0) service.GetFolder(@"\").DeleteFolder("Tiny Tracker", 0);
    });

    // Run elevated, by the uninstaller: every account's task, then the folder. Null once gone, or when there was none.
    public static string? RemoveAll() => Try(() =>
    {
        var service = Connect();
        var folder = FolderOf(service);
        if (folder is null) return;
        var tasks = folder.GetTasks(EnumHidden);
        var names = new List<string>();
        for (var i = 1; i <= tasks.get_Count(); i++) names.Add(tasks.get_Item(new Variant(i)).get_Name());
        foreach (var name in names) folder.DeleteTask(name, 0);
        service.GetFolder(@"\").DeleteFolder("Tiny Tracker", 0);
    });

    // For the uninstaller's tests.
    public static bool FolderExists()
    {
        try
        {
            return FolderOf(Connect()) is not null;
        }
        catch (Exception e) when (Failed(e))
        {
            return false;
        }
    }

    // True when this user has a task by its name, whatever it runs.
    public static bool Registered(SecurityIdentifier user)
    {
        try
        {
            return FolderOf(Connect()) is { } folder && TaskOf(folder, user) is not null;
        }
        catch (Exception e) when (Failed(e))
        {
            return false;
        }
    }

    // True when this user's task is there, enabled, and runs this helper as registered.
    public static bool Exists(string helperPath, SecurityIdentifier user)
    {
        try
        {
            var folder = FolderOf(Connect());
            if (folder is null) return false;
            var task = TaskOf(folder, user);
            if (task is null || !task.get_Enabled()) return false;
            var definition = task.get_Definition();
            var actions = definition.get_Actions();
            if (definition.get_Principal().get_RunLevel() != RunLevelHighest || actions.get_Count() != 1) return false;
            var action = actions.get_Item(1);
            return string.Equals(action.get_Path(), helperPath, StringComparison.OrdinalIgnoreCase) && action.get_Arguments() == ArgumentsFor(user);
        }
        catch (Exception e) when (Failed(e))
        {
            return false;
        }
    }

    // Starts the helper with no prompt. Null once started, else why not, such as 0x80070002 for a missing task.
    public static string? Run(SecurityIdentifier user, string pipeName) => Try(() =>
    {
        using var parameters = new Variant(pipeName);
        Connect().GetFolder(Folder).GetTask(NameFor(user)).RunEx(parameters, 0, 0, "");
    });

    private static ITaskService Connect()
    {
        var service = CreateComObject<ITaskService>(TaskScheduler);
        service.Connect(default, default, default, default);
        return service;
    }

    private static ITaskFolder? FolderOf(ITaskService service)
    {
        try
        {
            return service.GetFolder(Folder);
        }
        catch (Exception e) when (Failed(e))
        {
            return null;
        }
    }

    // Another user may create it at the same moment.
    private static ITaskFolder CreateFolder(ITaskService service)
    {
        try
        {
            using var security = new Variant(FolderSecurity);
            return service.GetFolder(@"\").CreateFolder("Tiny Tracker", security);
        }
        catch (Exception e) when (Failed(e))
        {
            return service.GetFolder(Folder);
        }
    }

    private static IRegisteredTask? TaskOf(ITaskFolder folder, SecurityIdentifier user)
    {
        try
        {
            return folder.GetTask(NameFor(user));
        }
        catch (Exception e) when (Failed(e))
        {
            return null;
        }
    }

    private static string? Try(Action change)
    {
        try
        {
            change();
            return null;
        }
        catch (Exception e) when (Failed(e))
        {
            return $"0x{e.HResult:X8}";
        }
    }

    // A missing task arrives as FileNotFoundException and a refusal as UnauthorizedAccessException.
    private static bool Failed(Exception e) =>
        e is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException;

    // Task Scheduler's interfaces, each up to the last member used; the others hold their places in the vtable.
    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("00020400-0000-0000-C000-000000000046")]
    internal partial interface IDispatch
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
    internal partial interface ITaskService : IDispatch
    {
        ITaskFolder GetFolder(string path);
        void GetRunningTasks();
        ITaskDefinition NewTask(int flags);
        void Connect(Variant server, Variant user, Variant domain, Variant password);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
    internal partial interface ITaskFolder : IDispatch
    {
        void get_Name();
        void get_Path();
        void GetFolder();
        void GetFolders();
        ITaskFolder CreateFolder(string name, Variant sddl);
        void DeleteFolder(string name, int flags);
        IRegisteredTask GetTask(string path);
        IRegisteredTaskCollection GetTasks(int flags);
        void DeleteTask(string name, int flags);
        void RegisterTask();
        IRegisteredTask RegisterTaskDefinition(string path, ITaskDefinition definition, int flags, Variant userId, Variant password, int logonType,
            Variant sddl);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("86627EB4-42A7-41E4-A4D9-AC33A72F2D52")]
    internal partial interface IRegisteredTaskCollection : IDispatch
    {
        int get_Count();
        IRegisteredTask get_Item(Variant index);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
    internal partial interface IRegisteredTask : IDispatch
    {
        string get_Name();
        void get_Path();
        void get_State();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_Enabled();
        void put_Enabled();
        void Run();
        IDispatch RunEx(Variant parameters, int flags, int sessionId, string user);
        void GetInstances();
        void get_LastRunTime();
        void get_LastTaskResult();
        void get_NumberOfMissedRuns();
        void get_NextRunTime();
        ITaskDefinition get_Definition();
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("F5BC8FC5-536D-4F77-B852-FBC1356FDEB6")]
    internal partial interface ITaskDefinition : IDispatch
    {
        IRegistrationInfo get_RegistrationInfo();
        void put_RegistrationInfo();
        void get_Triggers();
        void put_Triggers();
        ITaskSettings get_Settings();
        void put_Settings();
        void get_Data();
        void put_Data();
        IPrincipal get_Principal();
        void put_Principal();
        IActionCollection get_Actions();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("416D8B73-CB41-4EA1-805C-9BE9A5AC4A74")]
    internal partial interface IRegistrationInfo : IDispatch
    {
        void get_Description();
        void put_Description(string value);
        void get_Author();
        void put_Author(string value);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("D98D51E5-C9B4-496A-A9C1-18980261CF0F")]
    internal partial interface IPrincipal : IDispatch
    {
        void get_Id();
        void put_Id();
        void get_DisplayName();
        void put_DisplayName();
        void get_UserId();
        void put_UserId(string value);
        void get_LogonType();
        void put_LogonType(int value);
        void get_GroupId();
        void put_GroupId();
        int get_RunLevel();
        void put_RunLevel(int value);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("8FD4711D-2D02-4C8C-87E3-EFF699DE127E")]
    internal partial interface ITaskSettings : IDispatch
    {
        void get_AllowDemandStart();
        void put_AllowDemandStart([MarshalAs(UnmanagedType.VariantBool)] bool value);
        void get_RestartInterval();
        void put_RestartInterval();
        void get_RestartCount();
        void put_RestartCount();
        void get_MultipleInstances();
        void put_MultipleInstances(int value);
        void get_StopIfGoingOnBatteries();
        void put_StopIfGoingOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool value);
        void get_DisallowStartIfOnBatteries();
        void put_DisallowStartIfOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool value);
        void get_AllowHardTerminate();
        void put_AllowHardTerminate();
        void get_StartWhenAvailable();
        void put_StartWhenAvailable();
        void get_XmlText();
        void put_XmlText();
        void get_RunOnlyIfNetworkAvailable();
        void put_RunOnlyIfNetworkAvailable();
        void get_ExecutionTimeLimit();
        void put_ExecutionTimeLimit(string value);
    }

    // Only exec actions are made and read.
    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("02820E19-7B98-4ED2-B2E8-FDCCCEFF619B")]
    internal partial interface IActionCollection : IDispatch
    {
        int get_Count();
        IExecAction get_Item(int index);
        void get__NewEnum();
        void get_XmlText();
        void put_XmlText();
        IExecAction Create(int type);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("4C3D624D-FD6B-49A3-B9B7-09CB3CD3F047")]
    internal partial interface IExecAction : IDispatch
    {
        void get_Id();
        void put_Id();
        void get_Type();
        string get_Path();
        void put_Path(string value);
        string get_Arguments();
        void put_Arguments(string value);
        void get_WorkingDirectory();
        void put_WorkingDirectory(string? value);
    }
}
