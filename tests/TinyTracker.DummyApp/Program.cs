using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyTracker.DummyApp;

// A small app that the closing tests start from a temp folder, close and reopen. Its window is hidden, like a tray app's,
// unless --visible, which puts a tool window off screen. It closes when Windows asks, as at sign-out; --stubborn refuses,
// --no-window has nothing to ask, --child starts one more copy tagged "child", and --report <file> writes where it runs and
// whether it inherited TINYTRACKER_TEST_MARK.
internal static class Program
{
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x00000080;

    private static bool _stubborn;
    // Kept alive while Windows calls it.
    private static WindowProcedure? _procedure;

    [STAThread]
    private static int Main(string[] args)
    {
        _stubborn = args.Contains("--stubborn");
        var report = Array.IndexOf(args, "--report");
        if (report >= 0 && report + 1 < args.Length)
            File.WriteAllText(args[report + 1], Environment.CurrentDirectory + "\n" + Environment.GetEnvironmentVariable("TINYTRACKER_TEST_MARK"));
        if (args.Contains("--child")) Process.Start(Environment.ProcessPath!, ["--tag", "child"])?.Dispose();
        if (args.Contains("--no-window"))
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        _procedure = Procedure;
        var instance = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = instance,
            ClassName = "TinyTrackerDummy",
        };
        RegisterClassEx(ref windowClass);
        var visible = args.Contains("--visible");
        CreateWindowEx(visible ? WsExToolWindow : 0, "TinyTrackerDummy", "Example App", WsOverlappedWindow | (visible ? WsVisible : 0),
            -32000, -32000, 200, 100, 0, 0, instance, 0);
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return 0;
    }

    private static nint Procedure(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmQueryEndSession:
                return _stubborn ? 0 : 1;
            case WmEndSession:
                if (wParam != 0 && !_stubborn) PostQuitMessage(0);
                return 0;
            case WmClose:
                if (!_stubborn) DestroyWindow(window);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, nint window, uint first, uint last);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}
