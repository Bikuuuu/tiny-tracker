using System.Security.AccessControl;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// Silent mode's task (spec §6.6): its name, command line and permissions, and a user who has none.
public class SilentTaskTests
{
    private static readonly SecurityIdentifier Nobody = new("S-1-5-21-1000000000-2000000000-3000000000-1001");

    private const int FileGenericRead = 0x120089;
    private const int FileGenericExecute = 0x1200A0;
    private const int FileAllAccess = 0x1F01FF;

    [Fact]
    public void Task_IsOnePerUser_InTheAppsFolder()
    {
        Assert.Equal("Tiny Tracker Helper (S-1-5-21-1000000000-2000000000-3000000000-1001)", SilentTask.NameFor(Nobody));
        Assert.Equal(@"\Tiny Tracker", SilentTask.Folder);
    }

    [Fact]
    public void Task_GivesThePipesNameAsItsArgument() =>
        Assert.Equal("--pipe $(Arg0) --user S-1-5-21-1000000000-2000000000-3000000000-1001", SilentTask.ArgumentsFor(Nobody));

    [Fact]
    public void Task_CanBeReadAndRunByItsUser_AndChangedOnlyByAdministratorsAndSystem()
    {
        var security = new RawSecurityDescriptor(SilentTask.SecurityFor(Nobody));
        Assert.True(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        var aces = security.DiscretionaryAcl!.Cast<CommonAce>().Select(a => (a.SecurityIdentifier, a.AceQualifier, a.AccessMask)).ToHashSet();
        Assert.True(aces.SetEquals(
            [
                (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AceQualifier.AccessAllowed, FileAllAccess),
                (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), AceQualifier.AccessAllowed, FileAllAccess),
                (Nobody, AceQualifier.AccessAllowed, FileGenericRead | FileGenericExecute),
            ]), "The task's permissions aren't read and run for its user, and full for SYSTEM and Administrators.");
    }

    [Fact]
    public void Register_OnlyFromProgramFiles_ForTheUserTheHelperRunsAs()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tiny Tracker", "TinyTracker.Helper.exe");
        var other = new SecurityIdentifier("S-1-5-21-1000000000-2000000000-3000000000-1002");
        Assert.Null(SilentTask.WhyNotRegister(installed, Nobody, Nobody));
        Assert.Equal("another user", SilentTask.WhyNotRegister(installed, Nobody, other));
        Assert.Equal("not in Program Files", SilentTask.WhyNotRegister(@"D:\Tools\Tiny Tracker\TinyTracker.Helper.exe", Nobody, Nobody));
        Assert.Equal("not in Program Files", SilentTask.WhyNotRegister(Environment.ProcessPath!, Nobody, Nobody));
    }

    [Fact]
    public void UserWithoutATask_HasNone() => Assert.False(SilentTask.Exists(@"C:\Program Files\Tiny Tracker\TinyTracker.Helper.exe", Nobody));

    [Fact]
    public void RunningATaskThatIsntThere_SaysSo() => Assert.Equal("0x80070002", SilentTask.Run(Nobody, HelperRules.NewPipeName()));
}
