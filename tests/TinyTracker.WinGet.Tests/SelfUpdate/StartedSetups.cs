using TinyTracker.WinGet.SelfUpdate;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// Keeps what each started Setup held, and starts nothing.
internal sealed class StartedSetups : ISetupSystem
{
    public List<byte[]> Started { get; } = [];

    public bool RunsInAnotherSession(string exe) => false;

    public bool OtherCopiesRun(string exe) => false;

    public void Start(string setup, IReadOnlyList<string> arguments) => Started.Add(File.ReadAllBytes(setup));
}
