using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace TinyTracker.App.Interop;

// Starts a program through the Windows shell, so it runs as the signed-in user, unelevated, with its arguments.
internal static partial class ShellLaunch
{
    private const int DesktopFolder = 0;
    private const int DesktopWindow = 8;
    private const int NeedDispatch = 1;
    private const uint Background = 0;
    private const int ShowNormal = 1;
    private static readonly Guid ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");

    // False when the shell can't, as with no Explorer desktop.
    public static bool Start(string program, string arguments, string folder)
    {
        try
        {
            var windows = NativeMethods.CreateComObject<IShellWindows>(ShellWindows);
            if (windows.FindWindowSW(new NativeMethods.VARIANT(DesktopFolder), default, DesktopWindow, out _, NeedDispatch) is not { } desktop) return false;
            var browser = desktop.QueryService(TopLevelBrowser, typeof(IShellBrowser).GUID);
            if (browser.QueryActiveShellView().GetItemObject(Background, Dispatch) is not { } view || view.get_Application() is not { } shell) return false;
            using var args = new NativeMethods.VARIANT(arguments);
            using var directory = new NativeMethods.VARIANT(folder);
            using var operation = new NativeMethods.VARIANT("");
            shell.ShellExecute(program, args, directory, operation, new NativeMethods.VARIANT(ShowNormal));
            return true;
        }
        // Whatever fails, the caller has a way that doesn't need the shell.
        catch (Exception)
        {
            return false;
        }
    }

    // Each interface goes up to the member called; the others hold their places in the vtable.
    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("00020400-0000-0000-C000-000000000046")]
    internal partial interface IDispatch
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    internal partial interface IShellWindows : IDispatch
    {
        void get_Count();
        void Item();
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        IServiceProvider? FindWindowSW(in NativeMethods.VARIANT location, in NativeMethods.VARIANT root, int windowClass, out int window, int options);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    internal partial interface IServiceProvider
    {
        IShellBrowser QueryService(in Guid service, in Guid id);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("000214E2-0000-0000-C000-000000000046")]
    internal partial interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("000214E3-0000-0000-C000-000000000046")]
    internal partial interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        IShellFolderViewDual? GetItemObject(uint item, in Guid id);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    internal partial interface IShellFolderViewDual : IDispatch
    {
        IShellDispatch2? get_Application();
    }

    // IShellDispatch's members, then IShellDispatch2's.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller),
        Options = ComInterfaceOptions.ComObjectWrapper), Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
    internal partial interface IShellDispatch2 : IDispatch
    {
        void get_Application();
        void get_Parent();
        void NameSpace();
        void BrowseForFolder();
        void Windows();
        void Open();
        void Explore();
        void MinimizeAll();
        void UndoMinimizeALL();
        void FileRun();
        void CascadeWindows();
        void TileVertically();
        void TileHorizontally();
        void ShutdownWindows();
        void Suspend();
        void EjectPC();
        void SetTime();
        void TrayProperties();
        void Help();
        void FindFiles();
        void FindComputer();
        void RefreshMenu();
        void ControlPanelItem();
        void IsRestricted();
        void ShellExecute(string file, NativeMethods.VARIANT arguments, NativeMethods.VARIANT folder, NativeMethods.VARIANT operation,
            NativeMethods.VARIANT show);
    }
}
