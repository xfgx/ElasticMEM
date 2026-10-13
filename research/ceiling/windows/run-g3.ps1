param(
  [Parameter(Mandatory=$true)][string]$RunId,
  [Parameter(Mandatory=$true)][string]$Scratch,
  [int]$Iterations=200, [int]$PoolMiB=32, [int]$AdapterIndex=0
)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..\..\..')
try {
  & python research/ceiling/run.py --id $RunId g3 --exe research/ceiling/windows/vram_bench.exe --scratch $Scratch --iterations $Iterations --pool-mib $PoolMiB --adapter-index $AdapterIndex
  if($LASTEXITCODE -ne 0){throw 'G3 failed/unsupported. Preserve report; do not treat partial rows as timing data.'}
  & python research/ceiling/verify.py "research/ceiling/runs/$RunId" --check-git
  if($LASTEXITCODE -ne 0){throw 'Run verification failed'}
} finally { Pop-Location }
