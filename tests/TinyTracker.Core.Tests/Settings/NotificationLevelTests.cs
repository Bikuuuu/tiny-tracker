using TinyTracker.Core.Settings;
using Xunit;

namespace TinyTracker.Core.Tests.Settings;

// Which toasts each level shows (spec §4.7).
public class NotificationLevelTests
{
    [Theory]
    [InlineData(ToastNews.Ready, true, true, false)]
    [InlineData(ToastNews.Permission, true, true, false)]
    [InlineData(ToastNews.Close, true, true, false)]
    [InlineData(ToastNews.Installed, true, false, false)]
    [InlineData(ToastNews.Failed, true, true, true)]
    [InlineData(ToastNews.Restart, true, true, false)]
    [InlineData(ToastNews.SelfAvailable, true, true, false)]
    [InlineData(ToastNews.SelfUpdated, true, false, false)]
    [InlineData(ToastNews.SelfFailed, true, true, true)]
    public void EachLevel_ShowsItsToasts(ToastNews news, bool all, bool needsMe, bool failures) =>
        Assert.Equal((all, needsMe, failures, false),
            (NotificationLevel.All.Shows(news), NotificationLevel.NeedsMe.Shows(news), NotificationLevel.Failures.Shows(news), NotificationLevel.Off.Shows(news)));
}
