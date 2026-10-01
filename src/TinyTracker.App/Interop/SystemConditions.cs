using System.Runtime.InteropServices;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Shell;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace TinyTracker.App.Interop;

// What the PC is doing, from Windows (spec §6.1, §6.2): network and Energy saver at start and on each change Windows reports,
// full screen and locked, which have no event, when asked. A read that fails counts as calm.
internal sealed class SystemConditions : ISystemConditions, IDisposable
{
    private const int NotPresent = 1, BusyFullScreen = 2, D3DFullScreen = 3, PresentationMode = 4, StoreApp = 7;

    private volatile bool _online = true;
    private volatile bool _metered;
    private volatile bool _batterySaver;
    private volatile bool _energySaverSetting;
    private readonly NativeMethods.PowerSettingCallback _energySaverCallback;
    private nint _energySaverHandle;

    public SystemConditions()
    {
        ReadNetwork();
        ReadEnergySaver();
        NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        // A plugged-in PC's Battery saver status never shows Windows 11 24H2's Energy saver; this setting does.
        // Windows before 24H2 doesn't know it, and Battery saver's status covers it there.
        _energySaverCallback = OnEnergySaverSetting;
        var recipient = new NativeMethods.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _energySaverCallback };
        var setting = NativeMethods.GUID_ENERGY_SAVER_STATUS;
        if (NativeMethods.PowerSettingRegisterNotification(ref setting, NativeMethods.DEVICE_NOTIFY_CALLBACK, ref recipient, out var handle) == 0)
            _energySaverHandle = handle;
    }

    public event EventHandler? Changed;

    public bool Online => _online;

    public bool BatterySaver => _batterySaver;

    public SystemState Read() => new(IsFullScreen(State()), _metered, _batterySaver, !_online);

    public bool Busy() => State() is var state && (state == NotPresent || IsFullScreen(state));

    public void Dispose()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
        PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged;
        if (_energySaverHandle != 0) NativeMethods.PowerSettingUnregisterNotification(_energySaverHandle);
        _energySaverHandle = 0;
    }

    private static int State() => NativeMethods.SHQueryUserNotificationState(out var state) == 0 ? state : 0;

    // A Store app in front counts only when it fills its screen, as a full-screen game does.
    private static bool IsFullScreen(int state) => state is BusyFullScreen or D3DFullScreen or PresentationMode || state == StoreApp && FrontFillsItsScreen();

    private static bool FrontFillsItsScreen()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0 || !NativeMethods.GetWindowRect(window, out var rect)) return false;
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var screen = info.rcMonitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    // Offline only without any connection to the internet: a network that merely fails Windows' own probe still tries.
    // Windows marks a connection metered per network, and roaming or a data limit counts too.
    private void ReadNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            _online = profile is not null && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
            _metered = profile?.GetConnectionCost() is { } cost
                && (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit);
        }
        catch (Exception)
        {
            _online = true;
            _metered = false;
        }
    }

    private void ReadEnergySaver() => _batterySaver = BatterySaverOn() || _energySaverSetting;

    private static bool BatterySaverOn()
    {
        try
        {
            return PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnNetworkChanged(object sender)
    {
        ReadNetwork();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnergySaverChanged(object? sender, object e)
    {
        ReadEnergySaver();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The first call carries the current value; the value follows the setting's GUID and length.
    private uint OnEnergySaverSetting(nint context, uint type, nint setting)
    {
        if (setting != 0) _energySaverSetting = Marshal.ReadInt32(setting, 20) != 0;
        ReadEnergySaver();
        Changed?.Invoke(this, EventArgs.Empty);
        return 0;
    }
}
