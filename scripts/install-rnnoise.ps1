# Install RNNoise noise suppression for REDE (Windows)
# Downloads rnnoise.dll and places it in ~/.rede/libs/

$ErrorActionPreference = "Stop"

$libsDir = Join-Path $env:USERPROFILE ".rede" "libs"
$libFile = Join-Path $libsDir "rnnoise.dll"

if (Test-Path $libFile) {
    Write-Host "[*] RNNoise already installed at $libFile"
    exit 0
}

New-Item -ItemType Directory -Force -Path $libsDir | Out-Null

# Download pre-built rnnoise.dll from the REDE release
$tag = "v2.18.21-beta"
$url = "https://github.com/caaatto/rede/releases/download/$tag/rnnoise.dll"

Write-Host "[*] Downloading rnnoise.dll..."
try {
    Invoke-WebRequest -Uri $url -OutFile $libFile -UseBasicParsing
    Write-Host "[+] RNNoise installed to $libFile"
    Write-Host "    Restart REDE to enable noise suppression."
} catch {
    Write-Host "[!] Download failed: $_"
    Write-Host "    You can manually place rnnoise.dll in $libsDir"
    exit 1
}
