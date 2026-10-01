<#
.SYNOPSIS
Publishes Tiny Tracker and builds its installer, dist\TinyTracker-Setup-<version>-x64.exe (spec §11).
.PARAMETER Version
The version stamped into the app and the installer; Directory.Build.props's by default.
.PARAMETER Dist
The folder it builds in, emptied first; dist by default.
.PARAMETER Iscc
Inno Setup 7.1's compiler.
#>
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = ([xml](Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version,
    [string]$Dist = (Join-Path (Split-Path $PSScriptRoot) 'dist'),
    [string]$Iscc = (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$app = Join-Path $Dist 'app'
$licenses = Join-Path $app 'licenses'
if (-not (Test-Path -LiteralPath $Iscc)) { throw "Inno Setup 7.1 isn't at $Iscc" }

if (Test-Path -LiteralPath $Dist) { Remove-Item -LiteralPath $Dist -Recurse -Force }
dotnet publish (Join-Path $root 'src\TinyTracker.App') -c Release -r win-x64 --self-contained -o $app -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

function Write-Text([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, ($Text -replace "`r?`n", "`r`n"), [Text.UTF8Encoding]::new($true))
}

# The license and notice files of every package the app ships, as its deps files list them, from the packages themselves.
$assets = Get-Content -LiteralPath (Join-Path $root 'src\TinyTracker.App\obj\project.assets.json') -Raw -Encoding utf8 | ConvertFrom-Json
$packageRoot = @($assets.packageFolders.PSObject.Properties.Name)[0]
$ours = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw -Encoding utf8
$permission = $ours.Substring($ours.IndexOf('Permission is hereby granted'))
Write-Text (Join-Path $licenses 'Tiny Tracker\LICENSE.txt') $ours
$shipped = Get-ChildItem -LiteralPath $app -Filter '*.deps.json' | ForEach-Object {
    (Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json).libraries.PSObject.Properties |
        Where-Object { $_.Value.type -in 'package', 'runtimepack' } | ForEach-Object { $_.Name -replace '^runtimepack\.' }
} | Sort-Object -Unique
foreach ($package in $shipped) {
    $id, $packageVersion = $package -split '/'
    $folder = Join-Path $packageRoot "$id\$packageVersion".ToLowerInvariant()
    $target = Join-Path $licenses "$id $packageVersion"
    $files = @(Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(licen[cs]e|notice|third[-_ ]?party[-_ ]?notices?)(\.(txt|md))?$')
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $files | Copy-Item -Destination $target
    if ($files.Name -match '^licen[cs]e') { continue }
    # A package with no license file names its license in its nuspec.
    $metadata = ([xml](Get-Content -LiteralPath (Join-Path $folder "$($id.ToLowerInvariant()).nuspec") -Raw -Encoding utf8)).package.metadata
    if ($metadata.license.type -eq 'expression' -and $metadata.license.'#text' -eq 'MIT') {
        Write-Text (Join-Path $target 'LICENSE.txt') "MIT License`n`n$($metadata.copyright)`n`n$permission"
    } elseif ($metadata.licenseUrl) {
        Write-Text (Join-Path $target 'LICENSE.txt') "$id $packageVersion`n$($metadata.copyright)`n`nLicense: $($metadata.licenseUrl)`n"
    } else {
        throw "No license found for $package"
    }
}

# The license page: ours, then the Microsoft terms of the Windows App SDK the app includes.
$sdk = Get-ChildItem -LiteralPath $licenses -Directory -Filter 'Microsoft.WindowsAppSDK.Foundation *' | Select-Object -First 1
if (-not $sdk) { throw "The Windows App SDK's license terms are missing" }
$terms = Get-Content -LiteralPath (Join-Path $sdk.FullName 'license.txt') -Raw -Encoding utf8
Write-Text (Join-Path $Dist 'license.txt') "Tiny Tracker`n`n$ours`n`n________________________________________`n`nTiny Tracker includes the Microsoft Windows App SDK, under these terms:`n`n$terms"

$defines = @("/DAppVersion=$Version", "/DDist=$Dist")
& $Iscc /Q @defines (Join-Path $root 'installer\TinyTracker.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer failed to compile' }
$setup = Join-Path $Dist "TinyTracker-Setup-$Version-x64.exe"
"$setup"
"SHA-256 $((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant())"
