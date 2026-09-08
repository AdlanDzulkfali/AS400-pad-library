<#
.SYNOPSIS
    Builds the AS400Automation library and AS400Daemon executable in Release mode for Power Automate Desktop.

.DESCRIPTION
    Compiles AS400Automation.csproj (net472 & netstandard2.0) and AS400Automation.Daemon.csproj (net472).
    Generates ready-to-deploy artifacts: AS400Automation.dll, AS400Daemon.exe, and distribution zip.

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
$libProject = Join-Path $scriptDir "AS400Automation.csproj"
$daemonProject = Join-Path (Split-Path -Parent $scriptDir) "AS400Automation.Daemon\AS400Automation.Daemon.csproj"

# Verify dotnet CLI
try {
    $dotnetVer = & dotnet --version
    Write-Host "[*] Found .NET SDK version: $dotnetVer" -ForegroundColor Green
} catch {
    Write-Error ".NET SDK is required to build this project. Please install .NET SDK 6.0, 8.0, or 9.0."
    exit 1
}

Write-Host "[*] Compiling library project ($libProject) in $Configuration mode..." -ForegroundColor Yellow
& dotnet build $libProject -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Library build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if (Test-Path $daemonProject) {
    Write-Host "[*] Compiling daemon project ($daemonProject) in $Configuration mode..." -ForegroundColor Yellow
    & dotnet build $daemonProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Daemon build failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

$net472Dir = Join-Path $scriptDir "bin\$Configuration\net472"
$net472Dll = Join-Path $net472Dir "AS400Automation.dll"
$net472Exe = Join-Path $net472Dir "AS400Daemon.exe"
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
}

if (Test-Path $net472Exe) {
    $item = Get-Item $net472Exe
    $hash = Get-FileHash -Path $net472Exe -Algorithm SHA256
    Write-Host "[OK] IPC Background Daemon:     " -ForegroundColor Green -NoNewline
    Write-Host $item.FullName
    Write-Host "     Size: $([math]::Round($item.Length / 1KB, 2)) KB" -ForegroundColor Gray
    Write-Host "     SHA256: $($hash.Hash)" -ForegroundColor Gray
}

if (Test-Path $netStdDll) {
    $item = Get-Item $netStdDll
    $hash = Get-FileHash -Path $netStdDll -Algorithm SHA256
    Write-Host "[OK] .NET Standard 2.0 DLL:    " -ForegroundColor Green -NoNewline
    Write-Host $item.FullName
    Write-Host "     Size: $([math]::Round($item.Length / 1KB, 2)) KB" -ForegroundColor Gray
    Write-Host "     SHA256: $($hash.Hash)" -ForegroundColor Gray
}

# Create deployment ZIP bundle
$zipPath = Join-Path $net472Dir "AS400Automation.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$filesToZip = @($net472Dll)
if (Test-Path $net472Exe) { $filesToZip += $net472Exe }
Compress-Archive -Path $filesToZip -DestinationPath $zipPath -Force

Write-Host "`n[ZIP] Distribution package created: $zipPath" -ForegroundColor Cyan

Write-Host @"

[SUCCESS] Build Complete!

DEPLOYMENT INSTRUCTIONS FOR POWER AUTOMATE DESKTOP:
1. Copy both files to a folder on your automation machine (e.g. C:\AS400Automation\):
   - AS400Automation.dll
   - AS400Daemon.exe  (Required for multi-step PowerShell actions)

2. If using separate 'Run PowerShell script' actions in PAD:
   - Always include in every step:
     Add-Type -Path "C:\AS400Automation\AS400Automation.dll"
     [AS400Automation.AS400Driver]::UseIpc = `$true

   - The DLL will automatically launch AS400Daemon.exe in the background.
   - The daemon automatically closes when PAD closes or after 5 minutes of idle time.
"@ -ForegroundColor Green
