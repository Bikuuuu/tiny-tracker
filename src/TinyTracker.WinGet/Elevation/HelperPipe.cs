using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyTracker.Core.Elevation;

namespace TinyTracker.WinGet.Elevation;

// The helper's end of the pipe (spec §8): the first and only instance of a name that's new each start, for one app.
// Only the app's user and Administrators may open it, and never over the network.
public static class HelperPipe
{
    // Throws UnauthorizedAccessException when another program took the name first.
    public static NamedPipeServerStream Create(string name, SecurityIdentifier user)
    {
        if (!HelperRules.IsPipeName(name)) throw new ArgumentException("That isn't a helper pipe name.", nameof(name));
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        // No buffers: a write ends once the other side has read it, so a helper that hangs up first loses nothing.
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0, 0, security);
    }
}
