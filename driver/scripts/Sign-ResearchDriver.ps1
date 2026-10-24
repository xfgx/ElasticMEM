# Only the exact native-linked research image verified in Server 2025 VM.
# Creates a non-exportable VM-local key. Does NOT change boot/security policy or install driver.
param([Parameter(Mandatory=$true)][string]$Path,[switch]$ResearchVM)
$ErrorActionPreference='Stop'
if(-not $ResearchVM){throw 'Disposable VM with a snapshot only. Pass -ResearchVM to acknowledge; never use your daily PC.'}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevated Administrator required.'}
$source=(Resolve-Path -LiteralPath $Path).Path
if([IO.Path]::GetExtension($source) -ne '.sys'){throw 'SYS required.'}
# Подписываем свежесобранный WDK-драйвер. Единственный запрет — отозванный битый LLD-образ (BugCheck 0x1A в VM).
$retiredLld='5362244D0D4D0724312EE3E403303190448FBDAF108B3D2A4A3A4B2CD6CEA8A5'
$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if($hash -eq $retiredLld){throw 'This is the retired LLD image (BugChecked in VM). Build with Build-Windows.ps1 (MSVC/WDK) and sign that instead.'}
Write-Host ('Signing SHA256 '+$hash)
$boot=(& bcdedit.exe /enum '{current}') -join "`n"
if($LASTEXITCODE -ne 0 -or $boot -notmatch 'testsigning\s+(Yes|Да)'){throw 'This VM must already allow standard test-signing. This script will not alter policy. If security policy prohibits it, stop.'}
(Get-Item -LiteralPath $source).IsReadOnly=$false
$cert=New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ElasticMem disposable VM research only' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable
$public=$source+'.test-public.cer'
Export-Certificate -Cert $cert -FilePath $public | Out-Null
Import-Certificate -FilePath $public -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
$signed=Set-AuthenticodeSignature -LiteralPath $source -Certificate $cert -HashAlgorithm SHA256
if($signed.Status -ne 'Valid' -or (Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid'){throw 'Signature/trust validation failed. Do not install.'}
Get-FileHash -LiteralPath $source -Algorithm SHA256
Write-Host ('VM-local research certificate thumbprint: '+$cert.Thumbprint)
Write-Warning 'VM-local test signature, not production approval. Private key remains non-exportable in this VM; do not share it. No driver installed; no boot policy changed.'
