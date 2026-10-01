namespace TinyTracker.Core.Inventory;

// What keeps an app up to date when winget can't, if that's known.
public enum UpdatedBy
{
    Unknown,
    Steam,
    MicrosoftStore,
    WindowsUpdate,
    DriverTool,
    // winget can't read its version; the app updates itself.
    ItSelf,
    // winget has packages of a similar name, but none it could match exactly.
    NoExactMatch,
}

// An installed app winget can update. LocalId is winget's id for its uninstall entry or MSIX package.
public sealed record InventoryApp(string Id, string Source, string Name, string Version, string Publisher, string LocalId);

// An installed app winget can't update.
public sealed record ElsewhereApp(string Name, string Version, string Publisher, string LocalId, UpdatedBy UpdatedBy);

public sealed record AppInventory(IReadOnlyList<InventoryApp> Trackable, IReadOnlyList<ElsewhereApp> Elsewhere);

// The installed apps for Choose apps.
public interface IAppInventory
{
    // Reports the plain list first, then returns it with the lookup matches moved into Trackable.
    // Throws PackageSourceException when winget can't answer.
    Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct);
}
