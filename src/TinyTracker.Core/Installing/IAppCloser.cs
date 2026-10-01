namespace TinyTracker.Core.Installing;

// Closes an app for Close & update and opens it again afterwards (spec §6.3). The App does it through Windows.
public interface IAppCloser
{
    // False when the app's install folder can't be found, or is one that's never used.
    bool CanClose(string localId);

    // Asks this user's processes running from the app's folder to close, the way installers do.
    IClosingApp Close(string localId);
}

// One app being closed.
public interface IClosingApp
{
    // Completes once every process it asked has exited.
    Task Closed { get; }

    // Ends those still running. False when one can't be ended, such as one running as admin.
    bool ForceClose();

    // Starts again what closed, unelevated, each with its own command line.
    void Reopen();
}
