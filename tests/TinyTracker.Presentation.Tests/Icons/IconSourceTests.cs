using TinyTracker.Presentation.Icons;
using Xunit;

namespace TinyTracker.Presentation.Tests.Icons;

public class IconSourceTests
{
    [Fact]
    public void MainProgram_IsTheOnlyOneLeft() =>
        Assert.Equal("editor.exe", IconSource.MainProgram(["unins000.exe", "editor.exe", "readme.txt", "UpdateHelper.exe"], "Example Editor"));

    [Fact]
    public void MainProgram_IsTheOneNamedAfterTheApp() =>
        Assert.Equal("ExampleEditor.exe", IconSource.MainProgram(["ExampleEditor.exe", "converter.exe"], "Example Editor 2.4"));

    [Fact]
    public void SeveralCandidates_MeanNoProgram() =>
        Assert.Null(IconSource.MainProgram(["one.exe", "two.exe"], "Example Editor"));

    [Fact]
    public void IconWithAlpha_IsPremultiplied()
    {
        byte[] pixels = [200, 100, 50, 128, 10, 20, 30, 0];
        IconSource.Premultiply(pixels, []);
        Assert.Equal([100, 50, 25, 128, 0, 0, 0, 0], pixels);
    }

    [Fact]
    public void IconWithoutAlpha_UsesItsMask()
    {
        byte[] pixels = [200, 100, 50, 0, 10, 20, 30, 0];
        byte[] mask = [0, 0, 0, 0, 255, 255, 255, 0];
        IconSource.Premultiply(pixels, mask);
        Assert.Equal([200, 100, 50, 255, 0, 0, 0, 0], pixels);
    }
}
