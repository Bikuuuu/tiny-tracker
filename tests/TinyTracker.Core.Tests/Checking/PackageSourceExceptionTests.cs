using System.Runtime.InteropServices;
using TinyTracker.Core.Checking;
using Xunit;

namespace TinyTracker.Core.Tests.Checking;

public class PackageSourceExceptionTests
{
    [Fact]
    public void Code_IsTheFailedCallsHResult()
    {
        var error = new PackageSourceException(CheckProblem.WinGetUnreachable, "winget stopped", new COMException("RPC", unchecked((int)0x800706BA)));
        Assert.Equal(CheckProblem.WinGetUnreachable, error.Problem);
        Assert.Equal("0x800706BA", error.Code);
    }

    [Fact]
    public void Code_IsNullWithoutAFailedCall() =>
        Assert.Null(new PackageSourceException(CheckProblem.WinGetTooOld, "winget 1.11.510 is older than 1.29.280.").Code);
}
