# Portable test for Gate0-Compare.ps1 using synthetic Gate0 files (no Windows counters needed).
param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop';$fails=0
$dir=Join-Path ([IO.Path]::GetTempPath()) ("gate0-test-"+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $dir|Out-Null
function New-Run($phase,$label,$inUse,$spread,$apps,$vmWs,$vmCount){
    $s=[ordered]@{in_use_bytes_median=$inUse;in_use_bytes_max=$inUse+$spread;committed_bytes_median=$inUse+1GB;pool_paged_bytes_median=300MB;memory_compression_working_set_median=200MB;
        apps_working_set_median=$apps;apps_process_count_median=$(if($phase -eq 'native'){12}else{0});vm_working_set_median=$vmWs;vm_process_count_median=$vmCount}
    $j=[ordered]@{schema='elasticmem.gate0.v1';phase=$phase;label=$label;summary=$s;samples=@([ordered]@{total_physical_bytes=16GB})}
    $f=Join-Path $dir "$phase-$label.json";$j|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $f;$f
}
function Check($ok,$what){if(-not $ok){$script:fails++;Write-Host "FAIL $what"}else{Write-Host "PASS $what"}}
$cmp=Join-Path $Root 'windows\Gate0-Compare.ps1';if(-not (Test-Path $cmp)){$cmp=Join-Path $Root 'windows/Gate0-Compare.ps1'}
$n1=New-Run native a 6GB 100MB 3GB 0 0;$n2=New-Run native b 6.1GB 100MB 3GB 0 0
$v1=New-Run vm a 8GB 100MB 0 4GB 2;$v2=New-Run vm b 8.2GB 100MB 0 4GB 2
$r=& $cmp -Native $n1,$n2 -Vm $v1,$v2|ConvertFrom-Json
Check ($r.verdict -eq 'vm_path_costs_more_ram') 'vm overhead detected'
Check ($null -eq $r.physical_ram_saved_bytes) 'no saving claimed when VM costs more'
Check ([math]::Abs([double]$r.metrics.in_use_bytes_median.vm_minus_native - 2.05GB) -lt 1MB) 'median delta'
$v3=New-Run vm c 5GB 100MB 0 3GB 2;$v4=New-Run vm d 5.1GB 100MB 0 3GB 2
$r=& $cmp -Native $n1,$n2 -Vm $v3,$v4|ConvertFrom-Json
Check ($r.verdict -eq 'vm_path_uses_less_ram' -and [int64]$r.physical_ram_saved_bytes -eq [int64](6.05GB-5.05GB)) 'saving counted only from matched medians'
$r=& $cmp -Native $n1 -Vm $v3|ConvertFrom-Json
Check ($r.verdict -eq 'vm_path_uses_less_ram' -and $null -eq $r.physical_ram_saved_bytes -and ($r.warnings -contains 'single_run_per_phase_noise_not_bounded')) 'single run never yields saving number'
$v5=New-Run vm e 6.05GB 300MB 0 3GB 2
$r=& $cmp -Native $n1 -Vm $v5|ConvertFrom-Json
Check ($r.verdict -eq 'inconclusive_difference_within_noise') 'noise bound'
$bad=$false;try{& $cmp -Native $v1 -Vm $n1|Out-Null}catch{$bad=$true};Check $bad 'phase mismatch rejected'
$v6=New-Run vm f 5GB 100MB 0 0 0
$r=& $cmp -Native $n1,$n2 -Vm $v6,$v3|ConvertFrom-Json
Check ($null -eq $r.physical_ram_saved_bytes -and ($r.warnings -contains 'vm_run_without_vm_process:f')) 'vm run without vm process flagged'
Remove-Item -Recurse -Force $dir
if($fails){Write-Host "GATE0 COMPARE TEST FAILED $fails";exit 1}else{Write-Host 'GATE0 COMPARE TEST PASS (synthetic files, not Windows counters)'}
