namespace TinyTracker.WinGet.Cli;

// What winget's command line says as it works (spec §6.4). Its words follow the Windows language, but the line that starts a
// download has one shape in all of them: a word or two with no colon, then the installer's address.
public static class WinGetOutput
{
    // An installer that finished but needs a restart: winget exits with success and says so only in these words, one for each
    // language it speaks (its InstallFlowReturnCodeRebootRequiredToFinish), compared without the final stop.
    private static readonly string[] Restart =
    [
        "Restart your PC to finish installation",
        "Starten Sie den PC neu, um die Installation abzuschließen",
        "Reinicie el equipo para finalizar la instalación",
        "Redémarrez votre PC pour terminer l’installation",
        "Riavvia il PC per completare l'installazione",
        "PC を再起動してインストールを完了します",
        "PC를 다시 시작하여 설치를 완료합니다",
        "Reiniciar seu PC para terminar a instalação",
        "Перезапустите компьютер, чтобы завершить установку",
        "重启电脑以完成安装",
        "重新啟動您的電腦以完成安裝",
    ];

    public static bool AsksForRestart(string line)
    {
        var words = line.Trim().TrimEnd('.', '。');
        return Restart.Any(r => string.Equals(words, r, StringComparison.OrdinalIgnoreCase));
    }

    // The installer's address, when this line starts a download.
    public static Uri? DownloadOf(string line)
    {
        var at = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = line.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        // Details, such as an agreement's link, are indented or follow a label.
        if (at <= 0 || char.IsWhiteSpace(line[0]) || !char.IsWhiteSpace(line[at - 1]) || line.AsSpan(0, at).IndexOfAny(':', '：') >= 0) return null;
        return Uri.TryCreate(line[at..].Trim(), UriKind.Absolute, out var url) && url.Scheme is "https" or "http" && url.Host.Length > 0 ? url : null;
    }
}
