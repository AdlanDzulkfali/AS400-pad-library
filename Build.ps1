<#
.SYNOPSIS
    Builds the AS400Automation library in Release mode for Power Automate Desktop.

.DESCRIPTION
    Compiles AS400Automation.csproj targeting .NET Framework 4.7.2 and .NET Standard 2.0.
    Verifies output artifacts and displays assembly information and SHA256 checksums.

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.EXAMPLE
    .\Build.ps1
    .\Build.ps1 -Configuration Release
#>

[CmdletBinding()]
param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  AS400 / IBM i TN5250 Standalone Library Build Process" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $scriptDir "AS400Automation.csproj"

if (-not (Test-Path $projectFile)) {
    Write-Error "Project file not found: $projectFile"
    exit 1
}

# Verify dotnet CLI
try {
    $dotnetVer = & dotnet --version
    Write-Host "[*] Found .NET SDK version: $dotnetVer" -ForegroundColor Green
} catch {
    Write-Error ".NET SDK is required to build this project. Please install .NET SDK 6.0, 8.0, or 9.0."
    exit 1
}

Write-Host "[*] Compiling project in $Configuration mode..." -ForegroundColor Yellow
& dotnet clean $projectFile -c $Configuration | Out-Null
& dotnet build $projectFile -c $Configuration --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$net472Dll = Join-Path $scriptDir "bin\$Configuration\net472\AS400Automation.dll"
$netStdDll = Join-Path $scriptDir "bin\$Configuration\netstandard2.0\AS400Automation.dll"

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host "  Build Output Verification" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

if (Test-Path $net472Dll) {
    $item = Get-Item $net472Dll
    $hash = Get-FileHash -Path $net472Dll -Algorithm SHA256
    Write-Host "[OK] .NET Framework 4.7.2 DLL: " -ForegroundColor Green -NoNewline
    Write-Host $item.FullName
    Write-Host "     Size: $([math]::Round($item.Length / 1KB, 2)) KB" -ForegroundColor Gray
    Write-Host "     SHA256: $($hash.Hash)" -ForegroundColor Gray
} else {
    Write-Warning "Target net472 output not found at: $net472Dll"
}

if (Test-Path $netStdDll) {
    $item = Get-Item $netStdDll
    $hash = Get-FileHash -Path $netStdDll -Algorithm SHA256
    Write-Host "[OK] .NET Standard 2.0 DLL:    " -ForegroundColor Green -NoNewline
    Write-Host $item.FullName
    Write-Host "     Size: $([math]::Round($item.Length / 1KB, 2)) KB" -ForegroundColor Gray
    Write-Host "     SHA256: $($hash.Hash)" -ForegroundColor Gray
}

Write-Host "`n[SUCCESS] AS400Automation.dll is ready for Power Automate Desktop!" -ForegroundColor Green
Write-Host @"
Power Automate Desktop Usage:
  In PAD designer, add action 'Run PowerShell script' and call:
  Add-Type -Path "$net472Dll"
  `$sid = [AS400Automation.AS400Driver]::Connect("192.168.1.100", 23)
  [AS400Automation.AS400Driver]::WaitForText(`$sid, "Sign On", 15)
  ...
"@ -ForegroundColor Yellow
