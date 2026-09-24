<#
.SYNOPSIS
  Build KidsLocker as a single self-contained .exe (no .NET runtime needed on the target PC).
.DESCRIPTION
  Output: publish\KidsLockerSvc.exe. Copy that one file to the son's PC and run Install.ps1 there.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\KidsLocker.Service\KidsLocker.Service.csproj"
$outDir = Join-Path $root "publish"

# Locate dotnet (PATH, or the per-user install used during development).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { throw "dotnet SDK not found. Install .NET 8 SDK first." }

Write-Host "Publishing KidsLocker ($Configuration, $Runtime)..." -ForegroundColor Cyan
& $dotnet publish $proj -c $Configuration -r $Runtime `
    -p:PublishSingleFile=true -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir "KidsLocker.exe"
if (-not (Test-Path $exe)) { throw "expected $exe not found after publish" }

Write-Host "`nDone. Single-file app:" -ForegroundColor Green
Write-Host "  $exe"
Write-Host "`nNext: copy this one file to the target PC and run it (double-click)."
Write-Host "It opens the control panel: set the master password, configure Telegram, set the schedule, and press Install."
