# Install ClaudeBridge Add-In for ABB RobotStudio
# Run with: powershell -ExecutionPolicy Bypass -File install-addin.ps1
# Requires Administrator permissions to install into RobotStudio Bin\Addins.
#
# If RobotStudio is still running (or still releasing the DLL after you closed it), the script
# keeps retrying every few seconds and tells you what it is waiting for, for up to 60 s (-TimeoutSeconds 0 = no limit). Ctrl+C to give up.

param(
    [string]$RobotStudioVersion = "2026",
    [int]$RetryDelaySeconds = 3,
    [int]$TimeoutSeconds = 60   # 0 = retry until it works
)

$addinName = "ClaudeBridge"
$source = "$PSScriptRoot\addin\bin"
$rsBase = "${env:ProgramFiles(x86)}\ABB\RobotStudio $RobotStudioVersion"
$dest = "$rsBase\Bin\Addins\$addinName"
$commonDest = "${env:ProgramFiles(x86)}\Common Files\ABB\RobotStudio\Addins\$addinName"

# 1. Build if not already built
if (-not (Test-Path "$source\ClaudeBridge.dll")) {
    Write-Host "Building ClaudeBridge Add-In first..." -ForegroundColor Cyan
    dotnet build "$PSScriptRoot\addin\ClaudeBridge.csproj" -c Release
    if (-not (Test-Path "$source\ClaudeBridge.dll")) {
        Write-Error "Failed to build ClaudeBridge.dll. Ensure .NET SDK is installed."
        exit 1
    }
}

# 2. Verify RobotStudio installation
if (-not (Test-Path "$rsBase\Bin\ABB.Robotics.RobotStudio.dll")) {
    Write-Error "RobotStudio $RobotStudioVersion not found at: $rsBase"
    exit 1
}

# 3. Administrator rights are needed to write under Program Files: no point in retrying without them
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "WARNING: this PowerShell is not running as Administrator. Copying under Program Files will probably be refused." -ForegroundColor Yellow
}

# 4. Deploy, retrying until RobotStudio has really released ClaudeBridge.dll
$files = @("$source\ClaudeBridge.dll", "$PSScriptRoot\addin\ClaudeBridge.rsaddin")
$start = Get-Date
$attempt = 0
$installed = $false
while (-not $installed) {
    $attempt++
    $stamp = Get-Date -Format "HH:mm:ss"

    if (Get-Process -Name "RobotStudio" -ErrorAction SilentlyContinue) {
        Write-Host "[$stamp] Attempt ${attempt}: RobotStudio is still running - waiting for it to close..." -ForegroundColor Yellow
    } else {
        $failure = $null
        $denied = $false
        foreach ($target in @($dest, $commonDest)) {
            try {
                New-Item -ItemType Directory -Path $target -Force -ErrorAction Stop | Out-Null
                foreach ($f in $files) { Copy-Item $f $target -Force -ErrorAction Stop }
                # Verify the copy: the DLL on disk must be identical to the built one
                $a = (Get-FileHash "$source\ClaudeBridge.dll").Hash
                $b = (Get-FileHash "$target\ClaudeBridge.dll").Hash
                if ($a -ne $b) { throw "Copied DLL differs from the built one in $target" }
            } catch {
                $failure = $_.Exception.Message
                if ($_.Exception -is [UnauthorizedAccessException] -and -not $isAdmin) { $denied = $true }
                break
            }
        }
        if (-not $failure) { $installed = $true; break }
        if ($denied) {
            Write-Error ("Access denied while copying and this PowerShell is not Administrator.`n  Cause: $failure`n  Reopen PowerShell as Administrator and run the script again.")
            exit 1
        }
        Write-Host "[$stamp] Attempt ${attempt}: files still locked or not writable ($failure) - retrying..." -ForegroundColor Yellow
    }

    if ($TimeoutSeconds -gt 0 -and ((Get-Date) - $start).TotalSeconds -ge $TimeoutSeconds) {
        Write-Error "Gave up after $TimeoutSeconds s. Close RobotStudio completely (check Task Manager for RobotStudio.exe) and run the script again."
        exit 1
    }
    Start-Sleep -Seconds $RetryDelaySeconds
}

Write-Host ""
Write-Host "=== ClaudeBridge Add-In Installed Successfully (attempt $attempt) ===" -ForegroundColor Green
Write-Host "  Source:  $source"
Write-Host "  Target:  $dest"
Write-Host "  Version: 2.0.0 (TcpListener)"
Write-Host "  Port:    58080"
Write-Host "  AutoLoad: true"
Write-Host ""
Write-Host "You can start RobotStudio now." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Start RobotStudio and open the station"
Write-Host "2. RobotStudio Output Window will show: 'ClaudeBridge HTTP server started on port 58080'"
Write-Host "3. Verify connection: curl http://localhost:58080/ping"
Write-Host ""
