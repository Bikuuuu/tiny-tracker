namespace TinyTracker.Core.Installing;

public enum UpgradeStage
{
    Queued,
    Downloading,
    Installing,
    Finishing,
}

// Bytes are 0 when unknown. Fractions run from 0 to 1.
public readonly record struct UpgradeProgress(UpgradeStage Stage, ulong BytesDownloaded, ulong BytesRequired, double DownloadFraction, double InstallFraction)
{
    // winget can stop an upgrade only before its installer starts.
    public bool CanCancel => Stage is UpgradeStage.Queued or UpgradeStage.Downloading;
}
