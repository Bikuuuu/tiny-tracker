namespace TinyTracker.Core.Tracking;

// A package in a catalog. Compare with TrackedApp.Matches, which ignores case.
public readonly record struct PackageKey(string Id, string Source);
