param([Parameter(Mandatory=$true)][string]$ScratchDirectory)
$ErrorActionPreference='Stop'
# Correctness injections, NOT performance measurements. Run AFTER a successful normal build/probe.
$base=Join-Path $env:TEMP ('elasticmem-negative-'+[guid]::NewGuid().ToString())
New-Item -ItemType Directory $base | Out-Null
foreach($bytes in @(4096,65536,1048576,16777216)) {
  foreach($fault in @('skip-copy','wrong-offset')) {
    $out=Join-Path $base ($fault+'-'+$bytes)
    New-Item -ItemType Directory $out | Out-Null
    $scratch=Join-Path $ScratchDirectory ('elasticmem-'+[guid]::NewGuid().ToString()+'.tmp')
    & (Join-Path $PSScriptRoot 'vram_bench.exe') $out $scratch 10 32 0 $fault $bytes *> (Join-Path $out 'console.txt')
    if($LASTEXITCODE -ne 1){throw "Negative test did not fail as expected: $fault $bytes; inspect $out"}
    if(-not (Select-String -Quiet -Path (Join-Path $out 'console.txt') -Pattern 'GPU restore byte mismatch')) {throw "Failed for another reason, NOT an accepted negative check: $out"}
  }
}
Write-Output "Expected byte-check failures verified. Keep correctness artifacts: $base"
