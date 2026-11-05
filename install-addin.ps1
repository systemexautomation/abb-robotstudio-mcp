# Install ClaudeBridge Add-In for ABB RobotStudio
# Run with: powershell -ExecutionPolicy Bypass -File install-addin.ps1
# Requires Administrator permissions to install into RobotStudio Bin\Addins.

param(
    [string]$RobotStudioVersion = "2026"
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

# 3. RobotStudio keeps the loaded add-in DLL locked: refuse to copy while it runs
if (Get-Process -Name "RobotStudio" -ErrorAction SilentlyContinue) {
    Write-Error "RobotStudio is running and locks ClaudeBridge.dll. Close RobotStudio, then run this script again."
    exit 1
}

# 4. Deploy to Bin\Addins and Common Files\Addins
foreach ($target in @($dest, $commonDest)) {
    try {
        New-Item -ItemType Directory -Path $target -Force -ErrorAction Stop | Out-Null
        Copy-Item "$source\ClaudeBridge.dll" $target -Force -ErrorAction Stop
        Copy-Item "$PSScriptRoot\addin\ClaudeBridge.rsaddin" $target -Force -ErrorAction Stop
    } catch {
        Write-Error ("Cannot copy files to: $target`n  Cause: " + $_.Exception.Message +
            "`n  Hints: close RobotStudio (it locks ClaudeBridge.dll) and run PowerShell as Administrator.")
        exit 1
    }
}

Write-Host ""
Write-Host "=== ClaudeBridge Add-In Installed Successfully ===" -ForegroundColor Green
Write-Host "  Source:  $source"
Write-Host "  Target:  $dest"
Write-Host "  Version: 2.0.0 (TcpListener)"
Write-Host "  Port:    58080"
Write-Host "  AutoLoad: true"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Restart RobotStudio"
Write-Host "2. RobotStudio Output Window will show: 'ClaudeBridge HTTP server started on port 58080'"
Write-Host "3. Verify connection: curl http://localhost:58080/ping"
Write-Host ""
