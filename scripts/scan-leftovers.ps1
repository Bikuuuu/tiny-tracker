<#
.SYNOPSIS
Records what Tiny Tracker could leave on this PC, or compares with that record and lists what's new (spec §12).
.DESCRIPTION
-Record saves a snapshot: every file, registry entry and scheduled task named after Tiny Tracker, in the places programs install
and keep their data, and the entries of the folders and registry keys they install into. -Compare lists what's new since then.
Anything of Tiny Tracker's fails (exit 1). Windows' own records of a program that ran, such as crash reports or the Start
menu's tile cache, and other new entries are only listed. Take both the same way, elevated or not.
#>
param(
    [Parameter(Mandatory, ParameterSetName = 'Record')][string]$Record,
    [Parameter(Mandatory, ParameterSetName = 'Compare')][string]$Compare
)
$ErrorActionPreference = 'Stop'
$named = 'Tiny ?Tracker'

# Records Windows and the test tools keep of programs that ran; they outlive any uninstall.
$records = @(
    "$env:SystemRoot\Prefetch\"
    "$env:ProgramData\Microsoft\Windows\WER\"
    "$env:LOCALAPPDATA\CrashDumps\"
    "$env:LOCALAPPDATA\Microsoft\Windows\WER\"
    "$env:LOCALAPPDATA\Microsoft\Windows\Explorer\"
    "$env:LOCALAPPDATA\Microsoft\TestingPlatform\"
    "$env:APPDATA\Microsoft\Windows\Recent\"
    'HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\'
    'HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AppListBackup\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CloudStore\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\PushNotifications\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunNotification'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Start\'
    'HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\UFH\'
)
# Where programs install and keep their data, as deep as an installer or the app writes, and where this checkout and the runner
# build, which are left out.
$roots = @(
    @{ Path = $env:ProgramFiles; Depth = 2 }, @{ Path = ${env:ProgramFiles(x86)}; Depth = 2 }, @{ Path = $env:ProgramData; Depth = 3 }
    @{ Path = $env:APPDATA; Depth = 3 }, @{ Path = $env:LOCALAPPDATA; Depth = 3 }, @{ Path = $env:TEMP; Depth = 2 }
    @{ Path = [Environment]::GetFolderPath('CommonPrograms'); Depth = 3 }, @{ Path = [Environment]::GetFolderPath('Programs'); Depth = 3 }
    @{ Path = [Environment]::GetFolderPath('CommonDesktopDirectory'); Depth = 1 }, @{ Path = [Environment]::GetFolderPath('Desktop'); Depth = 1 }
    @{ Path = "$env:SystemRoot\Temp"; Depth = 2 }
) | ForEach-Object { [pscustomobject]$_ } | Where-Object { $_.Path -and (Test-Path -LiteralPath $_.Path) }
$skip = @((Split-Path $PSScriptRoot), $env:GITHUB_WORKSPACE, $env:RUNNER_TEMP, $env:RUNNER_TOOL_CACHE) | Where-Object { $_ }
$machineKeys = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths', 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache',
    'HKLM\SOFTWARE\Classes\AppUserModelId', 'HKLM\SOFTWARE\RegisteredApplications'

if (-not ('LeftoverWalk' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LeftoverWalk {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    struct FindData {
        public uint Attributes; public long Created; public long Accessed; public long Written; public uint SizeHigh; public uint SizeLow; public uint Reserved0; public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindFirstFileExW(string path, int level, out FindData data, int op, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool FindNextFileW(IntPtr find, out FindData data);
    [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);
    // Every entry under the roots, to their depths, whose name holds one of the words. Links are listed, not followed; skipped
    // folders aren't entered.
    public static List<string> Named(string[] roots, int[] depths, string[] skip, string[] words) {
        var found = new List<string>();
        var folders = new Stack<KeyValuePair<string, int>>();
        for (var i = 0; i < roots.Length; i++) folders.Push(new KeyValuePair<string, int>(roots[i], depths[i]));
        while (folders.Count > 0) {
            var folder = folders.Pop();
            FindData d;
            var find = FindFirstFileExW(@"\\?\" + folder.Key + @"\*", 1, out d, 0, IntPtr.Zero, 2);
            if (find == new IntPtr(-1)) continue;
            try {
                do {
                    if (d.Name == "." || d.Name == "..") continue;
                    var path = folder.Key + @"\" + d.Name;
                    foreach (var w in words) if (d.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { found.Add(path); break; }
                    if (folder.Value > 1 && (d.Attributes & 0x10) != 0 && (d.Attributes & 0x400) == 0 && Array.FindIndex(skip, s => string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) < 0)
                        folders.Push(new KeyValuePair<string, int>(path, folder.Value - 1));
                } while (FindNextFileW(find, out d));
            } finally { FindClose(find); }
        }
        return found;
    }
}
'@
}

# reg query prints each key it matched, then the matching values under it; cmd keeps its complaints about keys it can't read.
function Get-NamedRegistry([string]$Key) {
    $current = $null
    foreach ($line in @(cmd /c "reg query `"$Key`" /s /f Tiny 2>nul")) {
        if ($line -like 'HKEY_*') {
            $current = $line
            if ($line -match $named) { $line }
        } elseif ($current -and $line -match '^\s+\S' -and $line -match $named) {
            "$current :: $($line.Trim())"
        }
    }
}

function Get-Snapshot {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $files = [LeftoverWalk]::Named([string[]]@($roots.Path), [int[]]@($roots.Depth), [string[]]$skip, [string[]]@('TinyTracker', 'Tiny Tracker'))
    $fileTime = $clock.Elapsed.TotalSeconds
    $registry = @(Get-NamedRegistry 'HKCU') + @($machineKeys | ForEach-Object { Get-NamedRegistry $_ })
    $registryTime = $clock.Elapsed.TotalSeconds - $fileTime
    $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object { $_.TaskPath + $_.TaskName })
    $folders = @(
        $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:APPDATA, $env:LOCALAPPDATA, "$env:LOCALAPPDATA\Programs",
        [Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('Desktop')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $keys = 'HKCU:\Software', 'HKLM:\SOFTWARE', 'HKLM:\SOFTWARE\WOW6432Node', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    $runs = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    $snapshot = [pscustomobject]@{
        Elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Named = @($files) + $registry + @($tasks -match $named)
        Entries = @($folders | ForEach-Object { Get-ChildItem -LiteralPath $_ -Force -ErrorAction SilentlyContinue | ForEach-Object FullName }) +
            @($keys | ForEach-Object { Get-ChildItem -LiteralPath $_ -ErrorAction SilentlyContinue | ForEach-Object Name }) +
            @($runs | ForEach-Object { $key = $_; (Get-Item -LiteralPath $key -ErrorAction SilentlyContinue).Property | ForEach-Object { "$key :: $_" } }) +
            $tasks
    }
    'Scanned files in {0:0.0} s, the registry in {1:0.0} s' -f $fileTime, $registryTime | Write-Host
    $snapshot
}

$now = Get-Snapshot
if ($Record) {
    $now | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Record -Encoding utf8
    "Recorded $($now.Named.Count) named items and $($now.Entries.Count) entries in $Record"
    exit 0
}

$before = Get-Content -LiteralPath $Compare -Raw -Encoding utf8 | ConvertFrom-Json
if ($before.Elevated -ne $now.Elevated) { throw 'Compare the way the snapshot was taken: elevated or not.' }
function New-Since($Old, $New) {
    $seen = [Collections.Generic.HashSet[string]]::new([string[]]@($Old), [StringComparer]::OrdinalIgnoreCase)
    @($New | Where-Object { -not $seen.Contains($_) })
}
function Test-Record([string]$Item) { foreach ($r in $records) { if ($Item.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) { return $true } }; $false }
$entries = @(New-Since $before.Entries $now.Entries)
$newNamed = @(New-Since $before.Named $now.Named) + @($entries -match $named) | Select-Object -Unique
$left = @($newNamed | Where-Object { -not (Test-Record $_) })
$kept = @($newNamed | Where-Object { Test-Record $_ })
$other = @($entries | Where-Object { $_ -notmatch $named })

'Left by Tiny Tracker: ' + $left.Count
$left | ForEach-Object { "  $_" }
"Windows' own records of it (not counted): " + $kept.Count
$kept | ForEach-Object { "  $_" }
'Other new entries (not counted): ' + $other.Count
$other | ForEach-Object { "  $_" }
exit ([int]($left.Count -gt 0))
