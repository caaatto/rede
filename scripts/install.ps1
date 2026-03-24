#Requires -Version 5.1
$ErrorActionPreference = "Stop"

# Rede Desktop - Install Script (Windows)
# Clones the repo, builds the desktop client, and creates a shortcut.

$RepoUrl = "git@github.com:caaatto/rede.git"
$Branch = "v2"
$InstallDir = if ($env:REDE_INSTALL_DIR) { $env:REDE_INSTALL_DIR } else { "$env:LOCALAPPDATA\Rede" }

Write-Host "=== Rede Desktop Installer ===" -ForegroundColor Cyan
Write-Host ""

# Check dependencies
foreach ($cmd in @("git", "dotnet")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Write-Host "[!] Missing dependency: $cmd" -ForegroundColor Red
        switch ($cmd) {
            "dotnet" { Write-Host "    Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" }
            "git"    { Write-Host "    Install git: https://git-scm.com/download/win" }
        }
        exit 1
    }
}

# Check .NET version
$dotnetVersion = (dotnet --version 2>$null)
$major = [int]($dotnetVersion -split '\.')[0]
if ($major -lt 8) {
    Write-Host "[!] .NET 8+ required (found: $dotnetVersion)" -ForegroundColor Red
    exit 1
}

# Clone or update
if (Test-Path "$InstallDir\.git") {
    Write-Host "[*] Updating existing installation..."
    Set-Location $InstallDir
    git fetch origin $Branch
    git checkout $Branch
    git pull origin $Branch
} else {
    Write-Host "[*] Cloning repository..."
    git clone -b $Branch $RepoUrl $InstallDir
    Set-Location $InstallDir
}

# Build desktop client
Write-Host "[*] Building desktop client..."
Set-Location "$InstallDir\rede-client"
dotnet build Rede.sln -c Release --nologo -v q

# Create launcher batch file
$LauncherDir = "$env:LOCALAPPDATA\Rede\bin"
New-Item -ItemType Directory -Force -Path $LauncherDir | Out-Null

$LauncherPath = "$LauncherDir\rede.cmd"
@"
@echo off
cd /d "$InstallDir\rede-client"
dotnet run --project src\Rede.Desktop -c Release --no-build -- %*
"@ | Set-Content -Path $LauncherPath -Encoding ASCII

# Create Start Menu shortcut
$ShortcutDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$ShortcutDir\Rede.lnk")
$Shortcut.TargetPath = "dotnet"
$Shortcut.Arguments = "run --project src\Rede.Desktop -c Release --no-build"
$Shortcut.WorkingDirectory = "$InstallDir\rede-client"
$Shortcut.Description = "Secure E2EE Messenger"
$Shortcut.Save()

Write-Host ""
Write-Host "=== Installation complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "  Install dir:  $InstallDir"
Write-Host "  Launcher:     $LauncherPath"
Write-Host "  Start Menu:   Rede"
Write-Host ""
Write-Host "  Run with:     rede"
Write-Host ""

# Check if launcher dir is in PATH
if ($env:PATH -notlike "*$LauncherDir*") {
    Write-Host "  [!] $LauncherDir is not in your PATH." -ForegroundColor Yellow
    Write-Host "      Add it via: System Settings > Environment Variables"
    Write-Host "      Or run:"
    Write-Host "      [Environment]::SetEnvironmentVariable('PATH', `$env:PATH + ';$LauncherDir', 'User')"
    Write-Host ""
}
