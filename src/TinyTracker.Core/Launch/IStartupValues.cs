namespace TinyTracker.Core.Launch;

// The per-user Run value named after the app, and the on/off mark Windows keeps for it (Task Manager's Startup apps).
public interface IStartupValues
{
    string? ReadRun();

    void WriteRun(string command);

    void DeleteRun();

    byte[]? ReadApproved();

    void DeleteApproved();
}
