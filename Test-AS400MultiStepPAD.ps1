<#
.SYNOPSIS
    Demonstrates Power Automate Desktop (PAD) multi-step execution across SEPARATE PowerShell processes.

.DESCRIPTION
    Power Automate Desktop runs each "Run PowerShell script" action in an isolated, ephemeral powershell.exe process.
    By enabling [AS400Automation.AS400Driver]::UseIpc = $true, sessions persist across actions via the AS400Daemon.exe
    background host, preventing the "AS400 session was not found" error.

.PARAMETER Simulate
    Runs against the built-in mock TN5250 server (no real AS400 mainframe required).

.PARAMETER Host
    Target AS400 IP or hostname.

.PARAMETER Port
    Target Telnet port (default: 23).

.EXAMPLE
    .\Test-AS400MultiStepPAD.ps1 -Simulate
    .\Test-AS400MultiStepPAD.ps1 -Host "192.168.1.100" -Port 23
#>

[CmdletBinding()]
param (
    [switch]$Simulate,
    [string]$HostName = "127.0.0.1",
    [int]$Port = 23
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dllPath = Join-Path $scriptDir "bin\Release\net472\AS400Automation.dll"
$exePath = Join-Path $scriptDir "bin\Release\net472\AS400Daemon.exe"

if (-not (Test-Path $dllPath)) {
    Write-Host "[*] Release binaries not found. Building now..." -ForegroundColor Yellow
    & (Join-Path $scriptDir "Build.ps1")
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  AS400 Multi-Step PAD Execution Demonstration" -ForegroundColor Cyan
Write-Host "  (Simulating 4 separate PAD 'Run PowerShell script' actions)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$mockServerJob = $null
$portFile = [System.IO.Path]::GetTempFileName()
if ($Simulate) {
    Write-Host "`n[*] Starting Mock TN5250 Server in background..." -ForegroundColor Yellow
    $mockServerJob = Start-Job -ScriptBlock {
        param($path, $outFile)
        Add-Type -Path $path
        $server = New-Object AS400Automation.Testing.MockTn5250Server(0)
        [System.IO.File]::WriteAllText($outFile, $server.Port.ToString())
        # Keep server alive until job is stopped
        while ($true) { Start-Sleep -Seconds 1 }
        $server.Dispose()
    } -ArgumentList $dllPath, $portFile

    # Wait for server port file to be populated
    $timeout = 50 # 5 seconds
    while ((Get-Item $portFile).Length -eq 0 -and $timeout -gt 0) {
        Start-Sleep -Milliseconds 100
        $timeout--
    }
    $Port = [int](Get-Content $portFile).Trim()
    Remove-Item $portFile -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Mock TN5250 Server listening on port: $Port" -ForegroundColor Green
    $HostName = "127.0.0.1"
}

function Invoke-PadAction([string]$code) {
    $bytes = [System.Text.Encoding]::Unicode.GetBytes($code)
    $encoded = [Convert]::ToBase64String($bytes)
    return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded)
}

try {
    # ------------------------------------------------------------------
    # PAD STEP 1: Connect action (Process 1)
    # ------------------------------------------------------------------
    Write-Host "`n>>> [PAD Action 1] Connect to AS400 (Process 1)..." -ForegroundColor Yellow
    $step1Script = @"
        Add-Type -Path '$dllPath'
        [AS400Automation.AS400Driver]::UseIpc = `$true
        `$sid = [AS400Automation.AS400Driver]::Connect('$HostName', $Port)
        [AS400Automation.AS400Driver]::WaitForText(`$sid, "Sign On", 10) | Out-Null
        Write-Output `$sid
"@
    $sessionId = (Invoke-PadAction $step1Script).Trim()
    Write-Host "[OK] Connected! Session ID = $sessionId (Stored in PAD variable %SessionId%)" -ForegroundColor Green

    # ------------------------------------------------------------------
    # PAD STEP 2: SendKeys / Enter credentials action (Process 2)
    # ------------------------------------------------------------------
    Write-Host "`n>>> [PAD Action 2] Send Credentials using %SessionId% (Process 2)..." -ForegroundColor Yellow
    $step2Script = @"
        Add-Type -Path '$dllPath'
        [AS400Automation.AS400Driver]::UseIpc = `$true
        `$sid = '$sessionId'
        [AS400Automation.AS400Driver]::SendKeys(`$sid, "DEMOUSER", "Tab") | Out-Null
        [AS400Automation.AS400Driver]::WriteAt(`$sid, 6, 53, "DEMOPASS") | Out-Null
        [AS400Automation.AS400Driver]::SendKey(`$sid, "Enter") | Out-Null
        [AS400Automation.AS400Driver]::WaitForText(`$sid, "MAIN MENU", 10) | Out-Null
        Write-Output "SUCCESS"
"@
    $step2Result = (Invoke-PadAction $step2Script).Trim()
    Write-Host "[OK] Keystrokes sent successfully! State: $step2Result" -ForegroundColor Green

    # ------------------------------------------------------------------
    # PAD STEP 3: Read Screen / Field action (Process 3)
    # ------------------------------------------------------------------
    Write-Host "`n>>> [PAD Action 3] Read Screen & Fields (Process 3)..." -ForegroundColor Yellow
    $step3Script = @"
        Add-Type -Path '$dllPath'
        [AS400Automation.AS400Driver]::UseIpc = `$true
        `$sid = '$sessionId'
        `$menuTitle = [AS400Automation.AS400Driver]::ReadText(`$sid, 1, 28, 25)
        `$cursorRow = [AS400Automation.AS400Driver]::GetCursorRow(`$sid)
        `$cursorCol = [AS400Automation.AS400Driver]::GetCursorCol(`$sid)
        Write-Output "`$menuTitle|`$cursorRow,`$cursorCol"
"@
    $step3Output = Invoke-PadAction $step3Script
    $parts = $step3Output.Split('|')
    $menuTitle = $parts[0].Trim()
    $cursorPos = $parts[1].Trim()

    Write-Host "[OK] Screen Menu Title: '$menuTitle'" -ForegroundColor Green
    Write-Host "[OK] Terminal Cursor:   ($cursorPos)" -ForegroundColor Green

    # ------------------------------------------------------------------
    # PAD STEP 4: Disconnect & Cleanup action (Process 4)
    # ------------------------------------------------------------------
    Write-Host "`n>>> [PAD Action 4] Disconnect & Stop Daemon (Process 4)..." -ForegroundColor Yellow
    $step4Script = @"
        Add-Type -Path '$dllPath'
        [AS400Automation.AS400Driver]::UseIpc = `$true
        `$sid = '$sessionId'
        `$disc = [AS400Automation.AS400Driver]::Disconnect(`$sid)
        [AS400Automation.AS400Driver]::StopDaemon() | Out-Null
        Write-Output `$disc
"@
    $step4Result = (Invoke-PadAction $step4Script).Trim()
    Write-Host "[OK] Disconnected: $step4Result" -ForegroundColor Green

    Write-Host "`n============================================================" -ForegroundColor Cyan
    Write-Host "  All 4 separate PowerShell actions executed successfully!" -ForegroundColor Green
    Write-Host "  The session was maintained across processes without loss." -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Cyan

} finally {
    if ($mockServerJob) {
        Stop-Job $mockServerJob -ErrorAction SilentlyContinue
        Remove-Job $mockServerJob -Force -ErrorAction SilentlyContinue
    }
    # Ensure daemon is stopped
    Add-Type -Path $dllPath -ErrorAction SilentlyContinue
    [AS400Automation.AS400Driver]::StopDaemon() | Out-Null
}
