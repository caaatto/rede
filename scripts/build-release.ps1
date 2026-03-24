#Requires -Version 5.1
$ErrorActionPreference = "Stop"

# Rede Desktop - Build Release (Windows)
# Creates a self-contained executable for distribution.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir
$OutputDir = "$ProjectDir\dist"

# Detect platform
if ($env:REDE_RID) {
    $RID = $env:REDE_RID
} elseif ($IsLinux) {
    $RID = "linux-x64"
} elseif ($IsMacOS) {
    $RID = "osx-x64"
} else {
    $RID = "win-x64"
}

Write-Host "=== Building Rede Desktop ===" -ForegroundColor Cyan
Write-Host "  Platform: $RID"
Write-Host "  Output:   $OutputDir\$RID\"
Write-Host ""

Set-Location $ProjectDir

dotnet publish src\Rede.Desktop\Rede.Desktop.csproj `
    -c Release `
    -r $RID `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o "$OutputDir\$RID\"

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host ""
Get-ChildItem "$OutputDir\$RID\Rede.Desktop*" | Format-Table Name, @{N="Size";E={"{0:N1} MB" -f ($_.Length/1MB)}} -AutoSize
Write-Host ""
Write-Host "  Run: $OutputDir\$RID\Rede.Desktop.exe"
