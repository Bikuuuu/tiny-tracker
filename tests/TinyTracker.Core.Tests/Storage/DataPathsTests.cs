using TinyTracker.Core.Storage;
using Xunit;

namespace TinyTracker.Core.Tests.Storage;

public class DataPathsTests
{
    [Fact]
    public void CurrentUser_LivesInRoamingAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, "Tiny Tracker"), DataPaths.ForCurrentUser().Root);
    }

    [Fact]
    public void Files_SitUnderTheRoot()
    {
        var paths = new DataPaths(@"C:\Data");
        Assert.Equal(@"C:\Data\settings.json", paths.Settings);
        Assert.Equal(@"C:\Data\history.json", paths.History);
        Assert.Equal(@"C:\Data\logs\app.log", paths.Log);
    }
}
