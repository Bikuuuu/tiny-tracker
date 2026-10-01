<#
.SYNOPSIS
The install test, on GitHub runners only (spec §12).
.DESCRIPTION
Takes a snapshot, checks that Setup refuses another folder, installs silently, runs the installed app's self-check,
upgrades over the running app, runs the runner-only helper tests against the installed helper, leaves what a user would,
and uninstalls silently. Then, in fresh installs of this code built with a lower version and of the previous release, the
app updates itself to this build from a stand-in GitHub; the first also refuses a release whose digest doesn't match. At the
end it checks that everything went and that no copy crashed, and compares with the snapshot. Every copy it closes must exit
with 0. The old Notepad++ and VLC must be installed first, as in CI's winget job.
.PARAMETER Setup
The installer to test.
.PARAMETER Version
The version it installs.
.PARAMETER Lower
This code's installer with a lower version.
.PARAMETER Previous
The previous release's installer; none before the first release.
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Lower,
    [string]$Previous
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'The install test installs and uninstalls for real, so it runs only on GitHub runners.' }
$root = Split-Path $PSScriptRoot
$work = Join-Path $env:RUNNER_TEMP 'install-test'
$folder = Join-Path $env:ProgramFiles 'Tiny Tracker'
$exe = Join-Path $folder 'TinyTracker.exe'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TinyTracker_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$toastKey = 'HKCU:\Software\Classes\AppUserModelId\TinyTracker'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Tiny Tracker.lnk'
$data = Join-Path $env:APPDATA 'Tiny Tracker'
$silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
$started = Get-Date
New-Item -ItemType Directory -Path $work -Force | Out-Null
Add-Type -Namespace InstallTest -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string className, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
'@

function Check([bool]$Ok, [string]$What) {
    if (-not $Ok) { throw "Failed: $What" }
    "ok: $What"
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $until) { return $false }
        Start-Sleep -Milliseconds 500
    }
    $true
}
function Get-App { @(Get-Process -Name TinyTracker -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe) }
function Get-RunValue { (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).'Tiny Tracker' }
function Get-TrayEntries {
    @(Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue |
        Where-Object { (Get-ItemProperty -LiteralPath $_.PSPath).ExecutablePath -like '*\Tiny Tracker\TinyTracker.exe' })
}
function Test-TrayWindow { [InstallTest.Native]::FindWindowW('TinyTracker.Tray', $null) -ne [IntPtr]::Zero }
function Get-ProxyOption {
    $export = (winget settings export) -join "`n"
    ($export.Substring($export.IndexOf('{')) | ConvertFrom-Json).adminSettings.ProxyCommandLineOptions
}
function Test-TaskFolder {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { $null = $service.GetFolder('\Tiny Tracker'); $true } catch { $false }
}
# Only the process itself: its tree holds the app that Setup starts again, which keeps running.
function Wait-Exit($Process, [int]$Seconds, [string]$Log, [string]$What) {
    if (-not $Process.WaitForExit($Seconds * 1000)) {
        Get-Content -LiteralPath "$work\$Log" -Tail 60 -ErrorAction SilentlyContinue
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        throw "Failed: $What ended within $Seconds s"
    }
    Check ($Process.ExitCode -eq 0) "$What exits with 0 (it gave $($Process.ExitCode))"
}
function Invoke-Setup([string]$Log, [string]$Installer = $Setup) {
    Wait-Exit (Start-Process -FilePath $Installer -ArgumentList ($silent + '/SP-' + "/LOG=`"$work\$Log`"") -PassThru) 600 $Log 'Setup'
}
function Invoke-Uninstall([string]$Log) {
    Wait-Exit (Start-Process -FilePath (Join-Path $folder 'unins000.exe') -ArgumentList ($silent + "/LOG=`"$work\$Log`"") -PassThru) 300 $Log 'The uninstaller'
}
# What the app's copies, Windows' crash records and the app's log say, kept with the logs too.
function Show-AppState([string]$Name) {
    Get-CimInstance Win32_Process -Filter "Name = 'TinyTracker.exe' OR Name = 'TinyTracker.Helper.exe' OR Name LIKE 'TinyTracker-Setup%' OR Name = 'WerFault.exe'" |
        ForEach-Object { "process $($_.ProcessId) since $($_.CreationDate): $($_.CommandLine)" }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started; Id = 1000, 1001, 1002, 1026 } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'TinyTracker' } | ForEach-Object { "event $($_.Id) at $($_.TimeCreated): $($_.Message)" }
    $logs = Join-Path $data 'logs'
    if (Test-Path -LiteralPath $logs) {
        Copy-Item -LiteralPath $logs -Destination (Join-Path $work "$Name-app-logs") -Recurse -Force
        Get-Content -LiteralPath (Join-Path $logs 'app.log') -Tail 80 -ErrorAction SilentlyContinue
    }
}
function Get-SetupVersion([string]$Installer) { (Split-Path $Installer -Leaf) -replace '^TinyTracker-Setup-(.+)-x64\.exe$', '$1' }
# A fresh install of that Setup updates itself to this build. The runner-only tests set up the stand-in GitHub, silent mode and
# Auto themselves, and remove what they set up.
function Test-SelfUpdate([string]$From, [string[]]$Tests) {
    $fromVersion = Get-SetupVersion $From
    "== Self-update from $fromVersion"
    Invoke-Setup "from-$fromVersion.log" $From
    Check ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq $fromVersion) "Tiny Tracker $fromVersion is installed"
    $env:TINYTRACKER_SELF_UPDATE_SETUP = $Setup
    $env:TINYTRACKER_SELF_UPDATE_VERSION = $Version
    $filters = $Tests | ForEach-Object { '--filter-method', "*.SelfUpdateRunnerTests.$_" }
    dotnet test --project (Join-Path $root 'tests\TinyTracker.WinGet.Tests') -c Release --no-build --fail-skips on --output Detailed -- @filters
    $passed = $LASTEXITCODE -eq 0
    if (-not $passed) { Show-AppState "from-$fromVersion" }
    Check $passed "Tiny Tracker $fromVersion updated itself to $Version"
    Remove-Item Env:\TINYTRACKER_SELF_UPDATE_SETUP, Env:\TINYTRACKER_SELF_UPDATE_VERSION
    Invoke-Uninstall "from-$fromVersion-uninstall.log"
    Check (Wait-Until { -not (Test-Path -LiteralPath $folder) -and -not (Test-Path -LiteralPath $uninstallKey) } 120) 'the program folder, its update folder with it, and the Settings > Apps entry are gone'
}
function Start-App {
    Start-Process -FilePath $exe -ArgumentList '--startup' | Out-Null
    Check (Wait-Until { (Get-App).Count -eq 1 -and (Test-TrayWindow) } 60) 'the app runs, with its tray window'
}
# Asked again until it's gone, since a copy that just started may have no tray window yet.
function Stop-App {
    $running = Get-App
    $running | ForEach-Object { $null = $_.Handle }
    Check (Wait-Until {
        $window = [InstallTest.Native]::FindWindowW('TinyTracker.Tray', $null)
        if ($window -ne [IntPtr]::Zero) { [InstallTest.Native]::PostMessageW($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        (Get-App).Count -eq 0
    } 30) 'the app quits'
    Check (-not ($running | Where-Object ExitCode -NE 0)) 'it exits with 0'
}

'== The runner tests build first, so their files are in neither snapshot'
dotnet build (Join-Path $root 'tests\TinyTracker.WinGet.Tests') -c Release -v q -nologo
Check ($LASTEXITCODE -eq 0) 'the runner tests build'

'== Snapshot'
& (Join-Path $PSScriptRoot 'scan-leftovers.ps1') -Record "$work\before.json"
Check ($LASTEXITCODE -eq 0) 'the snapshot is taken'

'== Another folder'
# Only Program Files keeps the elevated helper where only administrators can write.
$elsewhere = Join-Path $work 'elsewhere'
$refused = Start-Process -FilePath $Setup -ArgumentList ($silent + '/SP-' + "/DIR=`"$elsewhere`"" + "/LOG=`"$work\elsewhere.log`"") -PassThru
if (-not $refused.WaitForExit(120000)) { Stop-Process -Id $refused.Id -Force; throw 'Failed: Setup ended within 2 minutes' }
Check ($refused.ExitCode -eq 7) "Setup refuses another folder (it gave $($refused.ExitCode), 7 is the refusal)"
Check (-not (Test-Path -LiteralPath $elsewhere) -and -not (Test-Path -LiteralPath $uninstallKey)) 'and installs nothing'

'== A fresh silent install'
Invoke-Setup 'install.log'
Check (Test-Path -LiteralPath $exe) 'the app is in Program Files'
Check (Test-Path -LiteralPath "$folder\TinyTracker.Helper.exe") 'the helper is next to it'
Check (Test-Path -LiteralPath "$folder\licenses\Tiny Tracker\LICENSE.txt") 'the licenses folder is there'
Check (-not (Get-ChildItem -LiteralPath $folder -Filter '*.pdb')) 'no symbol files are installed'
Check (Test-Path -LiteralPath $shortcut) 'the Start menu entry is there'
$entry = Get-ItemProperty -LiteralPath $uninstallKey
Check ($entry.DisplayName -eq 'Tiny Tracker' -and $entry.DisplayVersion -eq $Version -and $entry.Publisher -eq 'Bikuuuu') "Settings > Apps lists Tiny Tracker $Version by Bikuuuu"
Check ((Get-RunValue) -eq "`"$exe`" --startup") 'Start with Windows is on'
Check ((Get-App).Count -eq 0) "the silent install didn't start the app"

"== The installed app's self-check"
$report = Join-Path $work 'self-check.json'
$check = Start-Process -FilePath $exe -ArgumentList '--self-check', "`"$report`"" -PassThru
if (-not $check.WaitForExit(120000)) { $check.Kill(); throw 'Failed: the self-check ended within 2 minutes' }
Check ($check.ExitCode -eq 0) "the self-check exits with 0 (it gave $($check.ExitCode))"
$result = Get-Content -LiteralPath $report -Raw
Check (($result | ConvertFrom-Json).trayAdded -and ($result | ConvertFrom-Json).pages.Count -eq 5) "the app adds its tray icon and loads every page: $result"

'== Start with Windows as SYSTEM'
# As a managed install's Setup runs it: SYSTEM's entry would start the app for no one.
$task = 'Tiny Tracker SYSTEM check'
$systemRun = 'Registry::HKEY_USERS\S-1-5-18\Software\Microsoft\Windows\CurrentVersion\Run'
Register-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute $exe -Argument '--start-with-windows') `
    -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount) -Force | Out-Null
Start-ScheduledTask -TaskName $task
Check (Wait-Until { (Get-ScheduledTaskInfo -TaskName $task).LastTaskResult -eq 0 } 60) '--start-with-windows ran as SYSTEM and ended with 0'
Unregister-ScheduledTask -TaskName $task -Confirm:$false
Check ($null -eq (Get-ItemProperty -LiteralPath $systemRun -ErrorAction SilentlyContinue).'Tiny Tracker') "SYSTEM's Run key has no entry"

'== An upgrade over the running app'
Start-App
Remove-ItemProperty -LiteralPath $runKey -Name 'Tiny Tracker'
$old = (Get-App)[0]
$null = $old.Handle
Invoke-Setup 'upgrade.log'
Check ($old.HasExited -and $old.ExitCode -eq 0) 'the running app quit cleanly for the upgrade'
Check (Wait-Until { $now = Get-App; $now.Count -eq 1 -and $now[0].Id -ne $old.Id } 60) 'it came back, one copy'
Check ($null -eq (Get-RunValue)) 'the upgrade left Start with Windows off'
Stop-App

"== The installed helper's work"
$env:TINYTRACKER_HELPER = Join-Path $folder 'TinyTracker.Helper.exe'
$env:TINYTRACKER_WINGET_UPGRADE_TESTS = '1'
dotnet test --project (Join-Path $root 'tests\TinyTracker.WinGet.Tests') -c Release --no-build --fail-skips on --output Detailed -- `
    --filter-method '*.ElevatedHelperTests.OldPackage_UpgradesThroughTheHelper' `
    --filter-method '*.ElevatedHelperTests.SilentTask_FromProgramFiles_StartsTheHelperWithNoPrompt_AndGoesAgain' `
    --filter-method '*.SpeedLimitRunnerTests.ProxyOption_TurnsOnThroughTheHelper' `
    --filter-method '*.SpeedLimitRunnerTests.LimitedUpgradeThroughTheHelper_CancelsCleanly_ThenStaysNearTheLimit'
Check ($LASTEXITCODE -eq 0) "the runner's helper tests pass with the installed helper"
Remove-Item Env:\TINYTRACKER_HELPER

'== What a user leaves behind'
# Settings that say the app turned winget's proxy option on, Start with Windows with Task Manager's mark, silent mode's task for
# this account and another, and a running app.
$settings = Join-Path $data 'settings.json'
$file = if (Test-Path -LiteralPath $settings) { Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$file | Add-Member -NotePropertyName turnedOnProxyOption -NotePropertyValue $true -Force
New-Item -ItemType Directory -Path $data -Force | Out-Null
[IO.File]::WriteAllText($settings, ($file | ConvertTo-Json -Depth 20))
Check ((Start-Process -FilePath $exe -ArgumentList '--start-with-windows' -Wait -PassThru).ExitCode -eq 0) '--start-with-windows exits with 0'
Check ((Get-RunValue) -eq "`"$exe`" --startup") 'it turned Start with Windows on'
if (-not (Test-Path -LiteralPath $approvedKey)) { New-Item -Path $approvedKey -Force | Out-Null }
New-ItemProperty -LiteralPath $approvedKey -Name 'Tiny Tracker' -PropertyType Binary -Value ([byte[]](2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) -Force | Out-Null
foreach ($sid in [Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-21-0-0-0-1001') {
    schtasks /create /tn "\Tiny Tracker\Tiny Tracker Helper ($sid)" /tr 'cmd.exe /c exit' /sc once /st 00:00 /f | Out-Null
    Check ($LASTEXITCODE -eq 0) "a silent-mode task for $sid is there"
}
Start-App
Check (Wait-Until { Test-Path -LiteralPath $toastKey } 30) 'its notification entry is there'
Check (Wait-Until { (Get-TrayEntries).Count -gt 0 } 30) 'its tray entry is there'
Check ((Get-ProxyOption) -eq $true) "winget's proxy option is on"

'== A silent uninstall'
$app = (Get-App)[0]
$null = $app.Handle
Invoke-Uninstall 'uninstall.log'
# Its copy in the temp folder may still be finishing.
Check (Wait-Until { -not (Test-Path -LiteralPath $folder) -and -not (Test-Path -LiteralPath $uninstallKey) } 120) 'the program folder and the Settings > Apps entry are gone'
Check (Select-String -LiteralPath "$work\uninstall.log" -SimpleMatch 'The cleanup ran through the Windows shell' -Quiet) 'it cleaned up through the Windows shell'
Check ($app.HasExited -and $app.ExitCode -eq 0) 'the running app closed, with 0'
Check ((Get-ProxyOption) -eq $false) "winget's proxy option is off again"
Check (-not (Get-ScheduledTask -TaskPath '\Tiny Tracker\' -ErrorAction SilentlyContinue)) 'no silent-mode task is left'
Check (-not (Test-TaskFolder)) 'the \Tiny Tracker\ task folder is gone'
Check ($null -eq (Get-RunValue)) 'the Run entry is gone'
Check ($null -eq (Get-ItemProperty -LiteralPath $approvedKey -ErrorAction SilentlyContinue).'Tiny Tracker') "Task Manager's mark is gone"
Check (-not (Test-Path -LiteralPath $toastKey)) 'the notification entry is gone'
Check ((Get-TrayEntries).Count -eq 0) 'the tray entry is gone'
Check (-not (Test-Path -LiteralPath $data)) 'the settings, history and logs are gone'
Check (-not (Test-Path -LiteralPath $shortcut)) 'the Start menu entry is gone'

Check ([version](Get-SetupVersion $Lower) -lt [version]$Version) "$(Get-SetupVersion $Lower) is lower than $Version"
Test-SelfUpdate $Lower 'InstalledApp_UpdatesItselfToThisBuild', 'WrongDigest_InstallsNothing'
if (-not $Previous) { '== Self-update from the previous release: there is none yet' }
elseif ([version](Get-SetupVersion $Previous) -ge [version]$Version) { "== Self-update from the previous release: $(Get-SetupVersion $Previous) isn't lower than $Version" }
else { Test-SelfUpdate $Previous 'InstalledApp_UpdatesItselfToThisBuild' }

'== No copy crashed'
# Windows logs every crash, of the copies Setup and the uninstaller start too.
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started; Id = 1000, 1026 } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'TinyTracker' })
$crashes | ForEach-Object { $_.Message }
Check ($crashes.Count -eq 0) 'Windows logged no crash of the app or its helper'

'== Leftover scan'
& (Join-Path $PSScriptRoot 'scan-leftovers.ps1') -Compare "$work\before.json"
Check ($LASTEXITCODE -eq 0) 'Tiny Tracker left nothing behind'
