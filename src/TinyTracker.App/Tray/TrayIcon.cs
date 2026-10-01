using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TinyTracker.App.Interop;

namespace TinyTracker.App.Tray;

// A message the tray window got and didn't use itself.
internal readonly record struct WindowMessage(uint Id, nint WParam, nint LParam);

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 1;
    // The uninstaller closes the app by asking this window.
    internal const string ClassName = "TinyTracker.Tray";
    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(120);
    private static NativeMethods.WndProc? s_wndProc;

    private readonly uint _taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    private readonly nint _hwnd;
    // Icons at the current size, loaded once each.
    private readonly Dictionary<string, nint> _icons = [];
    private readonly DispatcherQueueTimer _timer;
    private string[] _frames;
    private int _frame;
    private string _tooltip;

    public TrayIcon(string iconPath, string tooltip)
    {
        _frames = [iconPath];
        _tooltip = tooltip;
        s_wndProc = WndProc;
        var instance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        NativeMethods.RegisterClassExW(ref wc);
        // Hidden top-level window: message-only windows miss the TaskbarCreated broadcast.
        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = FrameTime;
        _timer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % _frames.Length;
            Notify(NativeMethods.NIM_MODIFY);
        };
    }

    public event EventHandler? Activated;
    public event EventHandler<int>? MenuCommand;
    public event EventHandler? CloseRequested;

    // Power, clock and region broadcasts reach this hidden top-level window, and so does the shortcut.
    public event EventHandler<WindowMessage>? Message;

    public nint Handle => _hwnd;

    public bool Added { get; private set; }

    // Read each time the menu opens, so it's always current.
    public Func<(int Id, string Text, bool Enabled)[]> Menu { get; set; } = () => [];

    public bool Show()
    {
        Added = Notify(NativeMethods.NIM_ADD) || Notify(NativeMethods.NIM_MODIFY);
        if (Added)
        {
            var data = Data(0);
            data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
        }
        return Added;
    }

    // One icon, or frames that loop while a check or install runs.
    public void Update(string[] frames, string tooltip)
    {
        if (!frames.SequenceEqual(_frames))
        {
            _frames = frames;
            _frame = 0;
        }
        _tooltip = tooltip;
        if (_frames.Length > 1) _timer.Start();
        else _timer.Stop();
        Notify(NativeMethods.NIM_MODIFY);
    }

    private NativeMethods.NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = Icon(_frames[_frame]),
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private bool Notify(uint message)
    {
        var data = Data(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        return NativeMethods.Shell_NotifyIconW(message, ref data);
    }

    private nint Icon(string path)
    {
        if (_icons.TryGetValue(path, out var icon)) return icon;
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForSystem());
        return _icons[path] = NativeMethods.LoadImageW(0, path, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
    }

    // A new display size needs icons of a new size.
    private void ForgetIcons()
    {
        foreach (var icon in _icons.Values) if (icon != 0) NativeMethods.DestroyIcon(icon);
        _icons.Clear();
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case (int)NativeMethods.WM_CONTEXTMENU:
                    ShowMenu((short)(ushort)(wParam & 0xFFFF), (short)(ushort)((wParam >> 16) & 0xFFFF));
                    break;
            }
            return 0;
        }
        if (msg == _taskbarCreated)
        {
            ForgetIcons();
            Show();
            return 0;
        }
        if (msg == NativeMethods.WM_CLOSE)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (msg == NativeMethods.WM_DISPLAYCHANGE && Added)
        {
            ForgetIcons();
            Notify(NativeMethods.NIM_MODIFY);
        }
        Message?.Invoke(this, new WindowMessage(msg, wParam, lParam));
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var (id, text, enabled) in Menu())
        {
            if (text == "-") NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
            else NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING | (enabled ? 0 : NativeMethods.MF_GRAYED), (nuint)id, text);
        }
        NativeMethods.SetForegroundWindow(_hwnd);
        WindowsMode.ApplyToMenus();
        var command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN, x, y, _hwnd, 0);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, 0, 0);
        NativeMethods.DestroyMenu(menu);
        if (command != 0) MenuCommand?.Invoke(this, (int)command);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (Added)
        {
            var data = Data(0);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            Added = false;
        }
        ForgetIcons();
        NativeMethods.DestroyWindow(_hwnd);
        NativeMethods.UnregisterClassW(ClassName, NativeMethods.GetModuleHandleW(null));
    }
}
