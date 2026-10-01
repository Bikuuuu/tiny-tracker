using Microsoft.Management.Deployment;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;

namespace TinyTracker.WinGet;

// What winget's install results and COM failures mean for the app. Codes from AppInstallerErrors.h (winget 1.29.380).
public static class ErrorMap
{
    private const int InvalidArguments = unchecked((int)0x8A150002);
    private const int CtrlSignal = unchecked((int)0x8A150005);
    private const int ShellExecuteFailed = unchecked((int)0x8A150006);
    private const int DownloadFailed = unchecked((int)0x8A150008);
    private const int NoApplicableInstaller = unchecked((int)0x8A150010);
    private const int HashMismatch = unchecked((int)0x8A150011);
    private const int NoPackageFound = unchecked((int)0x8A150014);
    private const int RequiresAdmin = unchecked((int)0x8A150019);
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int DownloadSizeMismatch = unchecked((int)0x8A15002E);
    private const int BlockedByPolicy = unchecked((int)0x8A15003A);
    private const int MsiInstallFailed = unchecked((int)0x8A150049);
    private const int VersionNotNewer = unchecked((int)0x8A15004F);
    private const int PortableInstallFailed = unchecked((int)0x8A150052);
    private const int ServiceUnavailable = unchecked((int)0x8A15006D);
    private const int ZeroByteInstaller = unchecked((int)0x8A150086);
    private const int PackageInUse = unchecked((int)0x8A150101);
    private const int InstallInProgress = unchecked((int)0x8A150102);
    private const int FileInUse = unchecked((int)0x8A150103);
    private const int MissingDependency = unchecked((int)0x8A150104);
    private const int DiskFull = unchecked((int)0x8A150105);
    private const int NotEnoughMemory = unchecked((int)0x8A150106);
    private const int NoNetwork = unchecked((int)0x8A150107);
    private const int RebootToFinish = unchecked((int)0x8A150109);
    private const int RebootToInstall = unchecked((int)0x8A15010A);
    private const int RebootStarted = unchecked((int)0x8A15010B);
    private const int InstallerCancelled = unchecked((int)0x8A15010C);
    private const int Downgrade = unchecked((int)0x8A15010E);
    private const int InstallBlockedByPolicy = unchecked((int)0x8A15010F);
    private const int Dependencies = unchecked((int)0x8A150110);
    private const int InUseByApplication = unchecked((int)0x8A150111);
    private const int SystemNotSupported = unchecked((int)0x8A150113);
    private const int UpgradeNotSupported = unchecked((int)0x8A150114);
    // HRESULT_FROM_WIN32(ERROR_CANCELLED): a UAC prompt was declined.
    private const int PromptDeclined = unchecked((int)0x800704C7);
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int RpcUnavailable = unchecked((int)0x800706BA);
    private const int RpcFailed = unchecked((int)0x800706BE);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int ServerExecFailure = unchecked((int)0x80080005);
    private const uint RestartRequired = 3010;
    private const uint RestartStarted = 1641;
    private const uint MsiBusy = 1618;
    private const uint InstallerPromptDeclined = 1223;

    public static UpgradeOutcome ForUpgrade(InstallResultStatus status, int error, uint installerError) =>
        Outcome(status == InstallResultStatus.Ok, status == InstallResultStatus.NoApplicableUpgrade, error, installerError, status switch
        {
            InstallResultStatus.DownloadError => UpgradeFailure.DownloadFailed,
            InstallResultStatus.BlockedByPolicy => UpgradeFailure.BlockedByPolicy,
            InstallResultStatus.NoApplicableInstallers => UpgradeFailure.NoApplicableInstaller,
            InstallResultStatus.CatalogError => UpgradeFailure.WinGetUnavailable,
            InstallResultStatus.InstallError => UpgradeFailure.InstallerFailed,
            _ => UpgradeFailure.Other,
        });

    // winget's command line exits with the codes COM reports (spec §6.4). With the arguments the app passes, the only one it can
    // refuse is --proxy, while its proxy option is off.
    public static UpgradeOutcome ForExitCode(int code) => code switch
    {
        InvalidArguments => new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.ProxyRefused, Code(code, 0)),
        NoPackageFound => new UpgradeOutcome(UpgradeResult.NotInstalled, Code: Code(code, 0)),
        CtrlSignal => new UpgradeOutcome(UpgradeResult.Cancelled),
        _ => Outcome(code == 0, false, code, 0,
            code is ShellExecuteFailed or MsiInstallFailed or PortableInstallFailed ? UpgradeFailure.InstallerFailed : UpgradeFailure.Other),
    };

    // The technical code behind Details, or null when there is none.
    public static string? Code(int error, uint installerError) => (error, installerError) switch
    {
        (0, 0) => null,
        (0, _) => $"installer {installerError}",
        (_, 0) => $"0x{error:X8}",
        _ => $"0x{error:X8}, installer {installerError}",
    };

    public static CheckProblem ForException(Exception error) => error switch
    {
        // An old winget lacks the newer COM interfaces.
        InvalidCastException => CheckProblem.WinGetTooOld,
        _ => error.HResult switch
        {
            ClassNotRegistered => CheckProblem.WinGetMissing,
            RpcUnavailable or RpcFailed or RpcDisconnected or ServerExecFailure or ServiceUnavailable => CheckProblem.WinGetUnreachable,
            _ => CheckProblem.Failed,
        },
    };

    // otherwise: the reason of a failure whose code says nothing more.
    private static UpgradeOutcome Outcome(bool ok, bool noUpdate, int error, uint installerError, UpgradeFailure otherwise)
    {
        var code = Code(error, installerError);
        // winget treats 3010 as success and 1641 as a failure; both need a restart.
        if (error is RebootToFinish or RebootToInstall or RebootStarted || installerError is RestartRequired or RestartStarted)
            return new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: code);
        if (ok) return new UpgradeOutcome(UpgradeResult.Updated);
        var result = error switch
        {
            PackageInUse or FileInUse or InUseByApplication => UpgradeResult.AppInUse,
            InstallInProgress => UpgradeResult.Busy,
            RequiresAdmin => UpgradeResult.NeedsAdmin,
            PromptDeclined => UpgradeResult.PermissionDeclined,
            UpdateNotApplicable or VersionNotNewer => UpgradeResult.NoUpdate,
            _ when installerError == MsiBusy => UpgradeResult.Busy,
            _ when installerError == InstallerPromptDeclined => UpgradeResult.PermissionDeclined,
            _ when noUpdate => UpgradeResult.NoUpdate,
            _ => UpgradeResult.Failed,
        };
        return result == UpgradeResult.Failed
            ? new UpgradeOutcome(result, Failure(error, otherwise), code)
            : new UpgradeOutcome(result, Code: code);
    }

    private static UpgradeFailure Failure(int error, UpgradeFailure otherwise) => error switch
    {
        HashMismatch => UpgradeFailure.HashMismatch,
        DownloadFailed or DownloadSizeMismatch or ZeroByteInstaller => UpgradeFailure.DownloadFailed,
        NoNetwork => UpgradeFailure.NoNetwork,
        DiskFull => UpgradeFailure.DiskFull,
        NotEnoughMemory => UpgradeFailure.NotEnoughMemory,
        BlockedByPolicy or InstallBlockedByPolicy => UpgradeFailure.BlockedByPolicy,
        NoApplicableInstaller => UpgradeFailure.NoApplicableInstaller,
        SystemNotSupported or UpgradeNotSupported => UpgradeFailure.NotSupported,
        MissingDependency or Dependencies => UpgradeFailure.MissingDependency,
        InstallerCancelled => UpgradeFailure.InstallerCancelled,
        Downgrade => UpgradeFailure.NewerInstalled,
        _ => otherwise,
    };
}
