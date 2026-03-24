#Requires -Version 5.1
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

# Rede — Create self-signed certificate for MSIX signing
# Must match the Publisher in AppxManifest.xml (CN=Rede)

$CertDir = "$PSScriptRoot\..\packaging\msix"
$PfxPath = "$CertDir\Rede.pfx"
$Subject = "CN=Rede"

if (Test-Path $PfxPath) {
    Write-Host "[*] Certificate already exists: $PfxPath" -ForegroundColor Yellow
    Write-Host "    Delete it first if you want to regenerate."
    exit 0
}

Write-Host "=== Creating self-signed code signing certificate ===" -ForegroundColor Cyan
Write-Host "  Subject: $Subject"
Write-Host ""

# Create certificate
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -FriendlyName "Rede MSIX Signing" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
    -NotAfter (Get-Date).AddYears(5)

Write-Host "[*] Certificate created: $($cert.Thumbprint)"

# Export to PFX (no password for build automation — keep this file secure!)
$pwd = ConvertTo-SecureString -String "rede-msix" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pwd | Out-Null

Write-Host "[*] Exported to: $PfxPath"
Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host ""
Write-Host "  To trust this cert on your machine (for sideloading):"
Write-Host "  Import-PfxCertificate -FilePath `"$PfxPath`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password (ConvertTo-SecureString 'rede-msix' -AsPlainText -Force)"
Write-Host ""
Write-Host "  IMPORTANT: Do NOT commit Rede.pfx to git!"
