# Installs an old package for the update tests; a try that fails or stalls, such as on a stuck mirror, starts again.
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$tries = 3
$minutes = 5
$arguments = "install --id $Id --version $Version --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"
for ($try = 1; $try -le $tries; $try++) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('winget', $arguments)
    $start.UseShellExecute = $false
    $winget = [System.Diagnostics.Process]::Start($start)
    if (-not $winget.WaitForExit($minutes * 60000)) {
        taskkill /PID $winget.Id /T /F | Out-Null
        $winget.WaitForExit()
        Write-Warning "winget ran past $minutes minutes, try $try of $tries."
    } elseif ($winget.ExitCode -eq 0) {
        exit 0
    } else {
        Write-Warning "winget exited with $($winget.ExitCode), try $try of $tries."
    }
}
throw "Couldn't install $Id $Version in $tries tries."
