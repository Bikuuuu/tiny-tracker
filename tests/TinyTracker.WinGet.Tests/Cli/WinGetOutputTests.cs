using TinyTracker.WinGet.Cli;
using Xunit;

namespace TinyTracker.WinGet.Tests.Cli;

// The line that starts a download has the same shape in every language winget speaks (spec §6.4).
public sealed class WinGetOutputTests
{
    [Theory]
    [InlineData("Downloading https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe")]
    [InlineData("Download läuft https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe")]
    [InlineData("Téléchargement en cours https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe")]
    [InlineData("ダウンロード中 https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe")]
    [InlineData("正在下载 https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe")]
    [InlineData("Скачивание https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe  ")]
    public void DownloadLine_GivesTheInstallersAddress(string line) =>
        Assert.Equal(new Uri("https://github.com/example/editor/releases/download/v2.5.0/editor-setup.exe"), WinGetOutput.DownloadOf(line));

    [Fact]
    public void AddressWithASpace_IsKeptWhole() =>
        Assert.Equal("https://example.com/Example%20Editor.exe", WinGetOutput.DownloadOf("Downloading https://example.com/Example Editor.exe")!.AbsoluteUri);

    [Fact]
    public void PlainAddress_Counts() => Assert.Equal(new Uri("http://127.0.0.1:5000/editor.exe"), WinGetOutput.DownloadOf("Downloading http://127.0.0.1:5000/editor.exe"));

    [Theory]
    [InlineData("Found Example Editor [Example.Editor] Version 2.5.0")]
    [InlineData("This application is licensed to you by its owner.")]
    [InlineData("Successfully verified installer hash")]
    [InlineData("  Agreement Url https://example.com/eula")]
    [InlineData("License Agreement: https://example.com/eula")]
    [InlineData("使用許諾契約：https://example.com/eula")]
    [InlineData("https://example.com/editor.exe")]
    [InlineData("Downloading ftp://example.com/editor.exe")]
    [InlineData("Downloading https://")]
    [InlineData("")]
    public void OtherLines_StartNoDownload(string line) => Assert.Null(WinGetOutput.DownloadOf(line));

    // An installer that finished but needs a restart: winget says so only in the Windows language, then exits with success.
    [Theory]
    [InlineData("Restart your PC to finish installation.")]
    [InlineData("Starten Sie den PC neu, um die Installation abzuschließen.")]
    [InlineData("Reinicie el equipo para finalizar la instalación.")]
    [InlineData("Redémarrez votre PC pour terminer l’installation.")]
    [InlineData("Riavvia il PC per completare l'installazione..")]
    [InlineData("PC を再起動してインストールを完了します。")]
    [InlineData("PC를 다시 시작하여 설치를 완료합니다.")]
    [InlineData("Reiniciar seu PC para terminar a instalação.")]
    [InlineData("Перезапустите компьютер, чтобы завершить установку.")]
    [InlineData("重启电脑以完成安装。")]
    [InlineData("重新啟動您的電腦以完成安裝。")]
    [InlineData("  Restart your PC to finish installation.  ")]
    public void RestartLine_IsSeenInEveryLanguageWinGetSpeaks(string line) => Assert.True(WinGetOutput.AsksForRestart(line));

    [Theory]
    [InlineData("Successfully installed")]
    [InlineData("Your PC will restart to finish installation.")]
    [InlineData("Successfully installed. Restart the application to complete the upgrade.")]
    [InlineData("")]
    public void OtherLines_AskForNoRestart(string line) => Assert.False(WinGetOutput.AsksForRestart(line));
}
