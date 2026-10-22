<#
.SYNOPSIS
    Builds a test-signed MSIX and installs it over the running app.

.DESCRIPTION
    Refuses to replace Crimson while a transfer appears active unless -Force is used.
#>
[CmdletBinding()]
param(
    [string]$Thumbprint = '09FB8E2FE8A6D3273F3AB1F9D4CCFB85EC88D5CD',
    [switch]$Force,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pfn = 'Foxtrot47.CrimsonLauncher_t4mcqp6q26c0g'
$stateFile = Join-Path $env:LOCALAPPDATA 'Crimson\localstate.json'

$busy = @{ 1 = 'Installing'; 6 = 'Repairing'; 8 = 'Updating' }

function Test-DownloadActive {
    if (-not (Test-Path $stateFile)) { return $null }
    $raw = Get-Content $stateFile -Raw

    foreach ($m in [regex]::Matches($raw, '"install_status"\s*:\s*(\d+)')) {
        $code = [int]$m.Groups[1].Value
        if ($busy.ContainsKey($code)) { return "install_status=$code ($($busy[$code]))" }
    }

    foreach ($m in [regex]::Matches($raw, '"install_path"\s*:\s*"((?:[^"\\]|\\.)*)"')) {
        $path = $m.Groups[1].Value -replace '\\\\', '\'
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $scratch = Join-Path $path '.Crimson'
        if (-not (Test-Path $scratch)) { continue }
        $cutoff = (Get-Date).AddSeconds(-90)
        $hot = Get-ChildItem $scratch -Filter *.chunk -ErrorAction SilentlyContinue |
               Where-Object { $_.LastWriteTime -gt $cutoff }
        if ($hot) { return "$($hot.Count) chunk file(s) written in the last 90s under $scratch" }
    }
    return $null
}

$active = Test-DownloadActive
if ($active) {
    if (-not $Force) {
        Write-Host "SKIPPED: a transfer looks active - $active" -ForegroundColor Yellow
        Write-Host "Re-run with -Force to deploy anyway (this will close Crimson)." -ForegroundColor Yellow
        exit 2
    }
    Write-Host "WARNING: deploying over an active transfer - $active" -ForegroundColor Red
}

Write-Host '==> building test-signed MSIX' -ForegroundColor Cyan
& dotnet build (Join-Path $repo 'Crimson.WinUI\Crimson.WinUI.csproj') `
    -c Release -r win-x64 -p:Platform=x64 `
    -p:EnablePackaging=true -p:EnableTestSigning=true `
    -p:PackageCertificateThumbprint=$Thumbprint `
    --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED' -ForegroundColor Red; exit 1 }

$pkg = Get-ChildItem (Join-Path $repo 'artifacts\msix-test') -Recurse -Filter *.msix |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $pkg) { Write-Host 'no .msix produced' -ForegroundColor Red; exit 1 }

$existing = Get-AppxPackage -Name 'Foxtrot47.CrimsonLauncher'
if ($existing) {
    Write-Host "==> removing $($existing.PackageFullName)" -ForegroundColor Cyan
    Remove-AppxPackage -Package $existing.PackageFullName
}

Write-Host "==> installing $($pkg.Name)" -ForegroundColor Cyan
Add-AppxPackage -Path $pkg.FullName -ForceApplicationShutdown

$installed = Get-AppxPackage -Name 'Foxtrot47.CrimsonLauncher'
Write-Host "==> installed $($installed.PackageFullName)" -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process "shell:AppsFolder\$pfn!App"
    Write-Host '==> launched' -ForegroundColor Green
}
