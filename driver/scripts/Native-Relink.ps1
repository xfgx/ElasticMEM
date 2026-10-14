# Research-only native Microsoft linker path. Does not sign/install or alter security.
param(
    [Parameter(Mandatory=$true)][string]$ObjectDirectory,
    [Parameter(Mandatory=$true)][string]$LinkerPath,
    [Parameter(Mandatory=$true)][string]$WdkRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$objects=(Resolve-Path -LiteralPath $ObjectDirectory).Path
$linker=(Resolve-Path -LiteralPath $LinkerPath).Path
$wdk=(Resolve-Path -LiteralPath $WdkRoot).Path
if ([IO.Path]::GetFileName($linker) -ne 'link.exe') { throw 'Use official Microsoft link.exe, not lld-link.' }
$signature=Get-AuthenticodeSignature -LiteralPath $linker
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'A valid Microsoft-signed link.exe from the official VS toolchain is required.'
}
$inputs=@(
    (Join-Path $objects 'driver.obj'), (Join-Path $objects 'em_store.obj'),
    (Join-Path $wdk 'Lib\wdf\kmdf\x64\1.33\wdfdriverentry.lib'),
    (Join-Path $wdk 'Lib\wdf\kmdf\x64\1.33\wdfldr.lib'),
    (Join-Path $wdk 'Lib\10.0.26100.0\km\x64\ntoskrnl.lib'),
    (Join-Path $wdk 'Lib\10.0.26100.0\km\x64\bufferoverflowfastfailk.lib')
)
foreach ($inputFile in $inputs) { if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Missing input: $inputFile" } }
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
$output=(Resolve-Path -LiteralPath $OutputDirectory).Path
$sys=Join-Path $output 'ElasticMemStore.sys'
$pdb=Join-Path $output 'ElasticMemStore.pdb'
if (Test-Path -LiteralPath $sys) { throw 'Output SYS already exists. Use a fresh build directory.' }
$linkArgs=@('/nologo','/dll','/driver','/subsystem:native,10.0','/entry:FxDriverEntry',
    '/nodefaultlib','/machine:x64','/dynamicbase','/highentropyva','/nxcompat',
    '/integritycheck','/guard:cf','/release','/brepro','/debug',"/pdb:$pdb","/out:$sys") + $inputs
& $linker @linkArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sys -PathType Leaf)) { throw 'Native link failed. Do not install partial output.' }
Get-FileHash -Algorithm SHA256 -LiteralPath $sys
Write-Warning 'UNSIGNED research SYS only. Native linker command passed Server 2025 smoke; this wrapper, stock MSBuild, Windows 11, HVCI and Verifier are not runtime-qualified.'
Write-Warning 'The tested object inputs produced LNK4078 (.data attribute mismatch); production qualification remains incomplete. No security settings changed.'
