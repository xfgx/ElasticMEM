param([Parameter(Mandatory)][string]$Path,[switch]$ResearchVM)
$ErrorActionPreference='Stop'
if (-not $ResearchVM) { throw 'Research-only driver. Snapshot a disposable Windows 11 VM first; pass -ResearchVM to acknowledge. Do not use your daily machine.' }
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated Administrator required.' }
$source=(Resolve-Path -LiteralPath $Path).Path
if ([IO.Path]::GetExtension($source) -ne '.sys') { throw 'Expected a SYS file.' }
$signature=Get-AuthenticodeSignature -LiteralPath $source
if ($signature.Status -ne 'Valid') { throw "Refusing unsigned/untrusted driver: $($signature.Status). No signing-enforcement or other security settings will be changed." }
if (Get-Service ElasticMemStore -ErrorAction SilentlyContinue) { throw 'ElasticMemStore service already exists. Close clients and uninstall the old test driver first.' }
$folder=Join-Path $env:ProgramData 'ElasticMemDriver'
if (Test-Path $folder) { throw 'Target directory already exists; inspect/remove the previous test installation manually.' }
New-Item -ItemType Directory -Path $folder | Out-Null
& icacls.exe $folder /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict driver directory ACL.' }
$dest=Join-Path $folder 'ElasticMemStore.sys'
Copy-Item -LiteralPath $source -Destination $dest
if ((Get-FileHash -LiteralPath $dest).Hash -ne (Get-FileHash -LiteralPath $source).Hash -or (Get-AuthenticodeSignature -LiteralPath $dest).Status -ne 'Valid') { throw 'Copied file failed signature/hash validation.' }
& sc.exe create ElasticMemStore type= kernel start= demand error= normal binPath= "`"$dest`"" DisplayName= ElasticMemStore
if ($LASTEXITCODE -ne 0) { throw 'Service creation failed.' }
& sc.exe start ElasticMemStore
if ($LASTEXITCODE -ne 0) { throw 'Windows rejected driver start. Do NOT disable protections. Collect error and remove the demand-start service with Uninstall-Driver.ps1.' }
Get-Service ElasticMemStore
Write-Host 'Loaded for this research VM only. Demand-start; not automatic on boot. Close all clients before stopping.'
