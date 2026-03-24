#Requires -Version 5.1
$ErrorActionPreference = "Stop"

# Rede Desktop — Build MSIX Package
# Run on Windows with Windows 10 SDK installed.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir
$PackagingDir = "$ProjectDir\packaging\msix"
$OutputDir = "$ProjectDir\dist\msix"
$PublishDir = "$OutputDir\publish"
$PfxPath = "$PackagingDir\Rede.pfx"
$PfxPassword = "rede-msix"

Write-Host "=== Building Rede MSIX Package ===" -ForegroundColor Cyan
Write-Host ""

# Check for certificate
if (-not (Test-Path $PfxPath)) {
    Write-Host "[!] No signing certificate found at: $PfxPath" -ForegroundColor Red
    Write-Host "    Run: powershell -ExecutionPolicy Bypass scripts\create-cert.ps1"
    exit 1
}

# Find Windows SDK tools
$sdkPaths = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
)
$makeAppx = $null
$signTool = $null

foreach ($sdkBase in $sdkPaths) {
    if (Test-Path $sdkBase) {
        $latest = Get-ChildItem $sdkBase -Directory | Where-Object { $_.Name -match "^\d+\." } | Sort-Object Name -Descending | Select-Object -First 1
        if ($latest) {
            $candidate = "$($latest.FullName)\x64\makeappx.exe"
            if (Test-Path $candidate) { $makeAppx = $candidate }
            $candidate = "$($latest.FullName)\x64\signtool.exe"
            if (Test-Path $candidate) { $signTool = $candidate }
        }
    }
}

if (-not $makeAppx) {
    Write-Host "[!] MakeAppx.exe not found. Install Windows 10 SDK." -ForegroundColor Red
    Write-Host "    https://developer.microsoft.com/windows/downloads/windows-sdk/"
    exit 1
}

Write-Host "[*] MakeAppx: $makeAppx"
Write-Host "[*] SignTool: $signTool"
Write-Host ""

# Step 1: Publish the app
Write-Host "[*] Publishing Rede.Desktop..." -ForegroundColor Cyan
Set-Location $ProjectDir

dotnet publish src\Rede.Desktop\Rede.Desktop.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $PublishDir

# Step 2: Create placeholder assets if they don't exist
$assetsDir = "$PublishDir\Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

# Generate minimal PNG assets if missing (1-pixel placeholders)
foreach ($asset in @(
    @{ Name = "StoreLogo.png"; Size = 50 },
    @{ Name = "Square44x44Logo.png"; Size = 44 },
    @{ Name = "Square150x150Logo.png"; Size = 150 },
    @{ Name = "Wide310x150Logo.png"; Size = 310 }
)) {
    $assetPath = "$assetsDir\$($asset.Name)"
    if (-not (Test-Path $assetPath)) {
        Write-Host "[*] Creating placeholder: $($asset.Name)"
        # Create minimal valid PNG (1x1 dark pixel)
        $pngHeader = [byte[]]@(
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,  # PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,  # IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,  # 1x1
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,  # 8bit RGB
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,  # IDAT chunk
            0x54, 0x08, 0xD7, 0x63, 0x60, 0x60, 0x60, 0x00,  # compressed
            0x00, 0x00, 0x04, 0x00, 0x01, 0x27, 0x34, 0x27,  # data
            0x0A, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,  # IEND chunk
            0x44, 0xAE, 0x42, 0x60, 0x82
        )
        [System.IO.File]::WriteAllBytes($assetPath, $pngHeader)
    }
}

# Step 3: Copy manifest
Copy-Item "$PackagingDir\AppxManifest.xml" "$PublishDir\AppxManifest.xml" -Force

# Step 4: Create MSIX
$msixPath = "$OutputDir\Rede.msix"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "[*] Packing MSIX..." -ForegroundColor Cyan
& $makeAppx pack /d $PublishDir /p $msixPath /o

if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] MakeAppx failed" -ForegroundColor Red
    exit 1
}

# Step 5: Sign MSIX
if ($signTool) {
    Write-Host "[*] Signing MSIX..." -ForegroundColor Cyan
    & $signTool sign /fd SHA256 /a /f $PfxPath /p $PfxPassword $msixPath

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[!] SignTool failed" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "=== MSIX build complete ===" -ForegroundColor Green
Write-Host ""
Get-Item $msixPath | Format-Table Name, @{N="Size";E={"{0:N1} MB" -f ($_.Length/1MB)}} -AutoSize
Write-Host ""
Write-Host "  Install: double-click $msixPath"
Write-Host ""
Write-Host "  If 'untrusted publisher' error, first trust the cert:"
Write-Host "  Import-PfxCertificate -FilePath `"$PfxPath`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password (ConvertTo-SecureString 'rede-msix' -AsPlainText -Force)"
