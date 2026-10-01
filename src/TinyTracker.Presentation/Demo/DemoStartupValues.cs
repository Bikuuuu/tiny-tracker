using TinyTracker.Core.Launch;

namespace TinyTracker.Presentation.Demo;

// Start with Windows in the demo: kept in memory, so the demo never touches the registry.
public sealed class DemoStartupValues : IStartupValues
{
    private string? _run;
    private byte[]? _approved;

    public string? ReadRun() => _run;

    public void WriteRun(string command) => _run = command;

    public void DeleteRun() => _run = null;

    public byte[]? ReadApproved() => _approved;

    public void DeleteApproved() => _approved = null;
}
