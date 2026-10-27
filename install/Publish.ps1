<#
.SYNOPSIS
  Собрать KidsLocker в один самодостаточный exe (на целевом ПК .NET не нужен).
.DESCRIPTION
  Результат: publish\KidsLocker.exe. Скопировать этот файл на ПК сына и запустить:
  откроется пульт, служба ставится в нём на вкладке «Служба».
  Релизы с готовым exe собирает GitHub Actions по тегу vX.Y.Z (.github/workflows/release.yml).
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
if (-not $dotnet) { throw "dotnet SDK not found. Install .NET 10 SDK first." }

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
