<#
.SYNOPSIS
Measures Tiny Tracker's idle budgets (spec §9) with the flyout closed: the CPU average, and memory, GPU and Efficiency mode at each
sample. Exits with 0 on PASS, 1 on FAIL, and 2 when the run says nothing: a check or an install ran meanwhile, no quiet minute came, or
a sample couldn't be read.
.PARAMETER Path
The copy to measure; the installed one by default.
.PARAMETER Minutes
How long it samples, after a quiet minute.
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:ProgramFiles 'Tiny Tracker\TinyTracker.exe'),
    [int]$Minutes = 30,
    [double]$CpuBudgetPercent = 0.1,
    [double]$MemoryBudgetMB = 40
)
$ErrorActionPreference = 'Stop'
$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
$app = @(Get-Process -Name TinyTracker -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Path })
if ($app.Count -ne 1) { throw "Tiny Tracker isn't running from $Path, or runs more than once" }
$app = $app[0]
$procId = $app.Id
$data = Join-Path $env:APPDATA 'Tiny Tracker'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class EcoQoS
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

    // Throttled execution speed, which Task Manager shows as Efficiency mode.
    public static bool IsOn(int id)
    {
        IntPtr process = OpenProcess(0x1000, false, id);
        if (process == IntPtr.Zero) return false;
        try
        {
            State state = new State();
            state.Version = 1;
            return GetProcessInformation(process, 4, ref state, Marshal.SizeOf(typeof(State))) && (state.ControlMask & state.StateMask & 1) != 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
'@

# A check or an install runs winget's COM server, its command line or the helper, or saves the app's files.
function Test-Busy { @(Get-Process -Name WindowsPackageManagerServer, winget, TinyTracker.Helper -ErrorAction SilentlyContinue).Count -gt 0 }
function Get-Saves { (Get-ChildItem -LiteralPath $data -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)=$($_.LastWriteTimeUtc.Ticks)" }) -join ';' }

"Measuring Tiny Tracker (process $procId) at $Path for $Minutes minutes, after a quiet minute"
$giveUp = (Get-Date).AddMinutes(10)
$quietSince = Get-Date
$saves = Get-Saves
while (((Get-Date) - $quietSince).TotalSeconds -lt 60) {
    if ((Get-Date) -gt $giveUp) { 'INCONCLUSIVE: no quiet minute in 10 minutes; run it again'; exit 2 }
    Start-Sleep -Seconds 5
    if ((Test-Busy) -or (Get-Saves) -ne $saves) { $quietSince = Get-Date; $saves = Get-Saves }
}

$app.Refresh()
$cpuStart = $app.TotalProcessorTime
$wallStart = Get-Date
$deadline = $wallStart.AddMinutes($Minutes)
$samples = [Collections.Generic.List[object]]::new()
$busy = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    if ($app.HasExited) { throw 'Tiny Tracker ended during the run' }
    $counter = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $procId" -Verbose:$false
    if ($null -eq $counter) { 'INCONCLUSIVE: its memory could not be read; run it again'; exit 2 }
    $memory = $counter.WorkingSetPrivate / 1MB
    $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -Filter "Name LIKE 'pid[_]$procId[_]%'" -Verbose:$false)
    $gpu = [double]($engines | Measure-Object -Property UtilizationPercentage -Sum).Sum
    $sample = [pscustomobject]@{ Memory = $memory; Gpu = $gpu; Eco = [EcoQoS]::IsOn($procId) }
    $samples.Add($sample)
    Write-Verbose ('{0:HH:mm:ss} memory {1:N1} MB, GPU {2:N1}%, Efficiency mode {3}' -f (Get-Date), $sample.Memory, $sample.Gpu, $sample.Eco)
    if ((Test-Busy) -or (Get-Saves) -ne $saves) { $busy = $true }
}
$app.Refresh()
$cpu = ($app.TotalProcessorTime - $cpuStart).TotalSeconds / ((Get-Date) - $wallStart).TotalSeconds / [Environment]::ProcessorCount * 100
$memoryPeak = ($samples | Measure-Object -Property Memory -Maximum).Maximum
$gpuPeak = ($samples | Measure-Object -Property Gpu -Maximum).Maximum
$ecoOn = @($samples | Where-Object Eco).Count

'CPU average : {0:N3}% (budget < {1}%)' -f $cpu, $CpuBudgetPercent
'Memory      : {0:N1} MB at most, as Task Manager shows it (budget < {1} MB)' -f $memoryPeak, $MemoryBudgetMB
'GPU         : {0:N1}% at most (budget 0%)' -f $gpuPeak
'Efficiency  : on at {0} of {1} samples' -f $ecoOn, $samples.Count
if ($busy) { 'INCONCLUSIVE: a check or an install ran; run it again'; exit 2 }
if ($cpu -lt $CpuBudgetPercent -and $memoryPeak -lt $MemoryBudgetMB -and $gpuPeak -eq 0 -and $ecoOn -eq $samples.Count) { 'PASS'; exit 0 }
'FAIL'
exit 1
