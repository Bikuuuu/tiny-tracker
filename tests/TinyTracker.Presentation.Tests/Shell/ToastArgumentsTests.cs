using TinyTracker.Presentation.Shell;
using Xunit;

namespace TinyTracker.Presentation.Tests.Shell;

// What a toast's click says to the app.
public sealed class ToastArgumentsTests
{
    [Theory]
    [InlineData(ToastAction.View)]
    [InlineData(ToastAction.UpdateAll)]
    [InlineData(ToastAction.Install)]
    [InlineData(ToastAction.CloseAndUpdate)]
    [InlineData(ToastAction.Later)]
    [InlineData(ToastAction.SelfUpdate)]
    [InlineData(ToastAction.WhatsNew)]
    public void EachAction_ComesBackFromItsArguments(ToastAction action) =>
        Assert.Equal(action, ToastArguments.ActionOf(ToastArguments.Of(action)));

    // Windows' own dismiss, clicked in the notification center, still reaches the app.
    [Fact]
    public void WindowsDismiss_IsLater_NotView() => Assert.Equal(ToastAction.Later, ToastArguments.ActionOf("dismiss"));

    [Fact]
    public void AClickOnTheBody_IsView() => Assert.Equal(ToastAction.View, ToastArguments.ActionOf(null));
}
