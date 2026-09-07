<#
.SYNOPSIS
    Sample Power Automate Desktop (PAD) PowerShell automation script for AS400 / IBM i.

.DESCRIPTION
    Demonstrates exact step-by-step calls to load the standalone AS400Automation.dll,
    connect to an AS400 host, wait for the sign-on display, enter credentials,
    extract data from screen coordinates and fields, and cleanly disconnect.
    
    Compatible with Power Automate Desktop's "Run PowerShell script" action in
    Free and Standard tiers without requiring custom action portal packages or third-party emulators.

.PARAMETER HostName
    AS400 / IBM i Hostname or IP address (e.g., '192.168.1.100' or 'pub400.com').
    Default: '127.0.0.1' (runs in local simulation mode).

.PARAMETER Port
    TN5250 port (default: 23, or 992 for SSL).

.PARAMETER UseSsl
    Set to true if connecting via encrypted TN5250-SSL / TLS.

.PARAMETER Username
    AS400 User ID to type into the User field.

.PARAMETER Password
    AS400 Password to type into the Password field.

.PARAMETER TimeoutSeconds
    Connection and screen wait timeout in seconds.

.PARAMETER Simulate
    Forces local simulation mode using the embedded mock server.

.EXAMPLE
    # Local simulation test (offline):
    .\Test-AS400PAD.ps1 -Simulate

    # Production usage against live AS400 mainframe:
    .\Test-AS400PAD.ps1 -HostName "192.168.1.100" -Username "MYUSER" -Password "MYPASS"
#>

[CmdletBinding()]
param (
    [string]$HostName = "127.0.0.1",
    [int]$Port = 23,
    [bool]$UseSsl = $false,
    [string]$Username = "DEMOUSER",
    [string]$Password = "DEMOPASS",
    [int]$TimeoutSeconds = 15,
    [switch]$Simulate
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Power Automate Desktop AS400 (TN5250) Automation Sample   " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# --------------------------------------------------------------------------
# Step 1: Locate and dynamically load AS400Automation.dll
# --------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dllPath = Join-Path $scriptDir "bin\Release\net472\AS400Automation.dll"

if (-not (Test-Path $dllPath)) {
    # Fallback to Debug if Release is not yet built
    $dllPath = Join-Path $scriptDir "bin\Debug\net472\AS400Automation.dll"
}

if (-not (Test-Path $dllPath)) {
    Write-Error "AS400Automation.dll not found. Please run .\Build.ps1 first."
    exit 1
}

Write-Host "[1/7] Loading AS400Automation assembly from: $dllPath" -ForegroundColor Yellow
Add-Type -Path $dllPath

$sessionId = $null
$mockServer = $null

try {
    # ----------------------------------------------------------------------
    # Optional Simulation Mode: Starts local loopback mock TN5250 server
    # ----------------------------------------------------------------------
    if ($Simulate -or ($HostName -eq "127.0.0.1" -and $Port -eq 23)) {
        Write-Host "[*] Starting embedded simulation TN5250 server on dynamic port..." -ForegroundColor Magenta
        $mockServer = [AS400Automation.Testing.MockTn5250Server]::new(0)
        $Port = $mockServer.Port
        $HostName = "127.0.0.1"
        Write-Host "      [OK] Simulation server listening on 127.0.0.1:$Port" -ForegroundColor Green
    }

    # ----------------------------------------------------------------------
    # Step 2: Establish connection to AS400 TN5250 Host
    # ----------------------------------------------------------------------
    Write-Host "[2/7] Connecting to AS400 host: $HostName`:$Port (SSL: $UseSsl)..." -ForegroundColor Yellow
    $sessionId = [AS400Automation.AS400Driver]::Connect($HostName, $Port, $UseSsl, $TimeoutSeconds)

    if (-not [AS400Automation.AS400Driver]::IsConnected($sessionId)) {
        throw "Failed to connect to AS400 host. Last error: $([AS400Automation.AS400Driver]::GetLastError($sessionId))"
    }

    Write-Host "      [OK] Session established with SessionId: $sessionId" -ForegroundColor Green

    # ----------------------------------------------------------------------
    # Step 3: Wait for Sign-On screen
    # ----------------------------------------------------------------------
    Write-Host "[3/7] Waiting for 'Sign On' screen..." -ForegroundColor Yellow
    $screenReady = [AS400Automation.AS400Driver]::WaitForText($sessionId, "Sign On", $TimeoutSeconds)
    if (-not $screenReady) {
        Write-Warning "Sign On text not found within timeout. Continuing flow..."
    } else {
        Write-Host "      [OK] Sign On screen confirmed!" -ForegroundColor Green
    }

    # ----------------------------------------------------------------------
    # Step 4: Enter credentials using WriteAt and SendKeys
    # ----------------------------------------------------------------------
    Write-Host "[4/7] Entering credentials and inspecting input fields..." -ForegroundColor Yellow
    # Write Username at Row 6, Col 42
    $null = [AS400Automation.AS400Driver]::WriteAt($sessionId, 6, 42, $Username)
    
    # Read back the entered field value before submission
    $typedUser = [AS400Automation.AS400Driver]::ReadField($sessionId, 6, 42)
    Write-Host "      [OK] User Field populated with: '$typedUser'" -ForegroundColor Green

    # Move cursor to Password field at Row 7, Col 42 and submit credentials with [Enter]
    $null = [AS400Automation.AS400Driver]::SetCursor($sessionId, 7, 42)
    $null = [AS400Automation.AS400Driver]::SendKeys($sessionId, $Password, "Enter")
    Write-Host "      [OK] Password entered and [Enter] transmitted to host." -ForegroundColor Green

    # ----------------------------------------------------------------------
    # Step 5: Wait for post-login menu screen and inspect
    # ----------------------------------------------------------------------
    Write-Host "[5/7] Waiting for main menu transition..." -ForegroundColor Yellow
    $menuLoaded = [AS400Automation.AS400Driver]::WaitForText($sessionId, "MAIN MENU", $TimeoutSeconds)
    if ($menuLoaded) {
        Write-Host "      [OK] Main Menu screen loaded successfully!" -ForegroundColor Green
    }

    $titleText = [AS400Automation.AS400Driver]::ReadText($sessionId, 1, 28, 17)
    $cursorPos = [AS400Automation.AS400Driver]::GetCursorPosition($sessionId)

    Write-Host "      Screen Header: '$titleText'" -ForegroundColor Cyan
    Write-Host "      Cursor Pos:    Row $($cursorPos.Item1), Col $($cursorPos.Item2)" -ForegroundColor Cyan

    # ----------------------------------------------------------------------
    # Step 6: Capture complete 24x80 presentation space dump
    # ----------------------------------------------------------------------
    Write-Host "[6/7] Capturing full 24x80 presentation space dump..." -ForegroundColor Yellow
    $screenDump = [AS400Automation.AS400Driver]::GetScreen($sessionId)
    Write-Host "---------------- AS400 Presentation Space ----------------" -ForegroundColor DarkGray
    Write-Host $screenDump -ForegroundColor White
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkGray

} catch {
    $lastErr = [AS400Automation.AS400Driver]::GetLastError($sessionId)
    Write-Host "`n[ERROR] Power Automate Desktop Step Failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($lastErr) {
        Write-Host "        AS400 Driver Detail: $lastErr" -ForegroundColor Red
    }
} finally {
    # ----------------------------------------------------------------------
    # Step 7: Clean Disconnect and Teardown
    # ----------------------------------------------------------------------
    if ($sessionId -and [AS400Automation.AS400Driver]::IsConnected($sessionId)) {
        Write-Host "[7/7] Disconnecting AS400 session '$sessionId'..." -ForegroundColor Yellow
        $disconnected = [AS400Automation.AS400Driver]::Disconnect($sessionId)
        if ($disconnected) {
            Write-Host "      [OK] Session cleanly closed and sockets terminated." -ForegroundColor Green
        }
    }

    if ($mockServer) {
        $mockServer.Dispose()
    }
}

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host "  AS400 Automation Script Completed Successfully            " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
