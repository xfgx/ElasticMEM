# Gate 0 comparison: matched native vs VM runs of the SAME scenario (ideally interleaved A B A B, reboot or
# identical warm state between runs). Pure computation over Gate0-Measure JSON files; changes nothing.
param(
    [Parameter(Mandatory=$true)][string[]]$Native,
    [Parameter(Mandatory=$true)][string[]]$Vm,
    [string]$Out
)
$ErrorActionPreference='Stop'
function Get-Median([double[]]$x){ if(-not $x -or $x.Count -eq 0){return $null}; $y=$x|Sort-Object; $c=$y.Count; if($c%2){return [double]$y[($c-1)/2]}; return ([double]$y[$c/2-1]+[double]$y[$c/2])/2 }
function Read-Runs([string[]]$files,[string]$phase){
    foreach($f in $files){
        $j=Get-Content -Raw -LiteralPath $f|ConvertFrom-Json
        if($j.schema -ne 'elasticmem.gate0.v1'){throw "Not a Gate0 file: $f"}
        if($j.phase -ne $phase){throw "Expected phase $phase in $f, got $($j.phase)"}
        $j
    }
}
$a=@(Read-Runs $Native 'native');$b=@(Read-Runs $Vm 'vm')
$totals=@($a+$b|ForEach-Object{[double]$_.samples[0].total_physical_bytes}|Sort-Object -Unique)
$warnings=New-Object System.Collections.Generic.List[string]
if($totals.Count -ne 1){$warnings.Add('different_total_physical_memory_between_runs')}
if($a.Count -ne $b.Count){$warnings.Add('unequal_number_of_native_and_vm_runs')}
if($a.Count -lt 2){$warnings.Add('single_run_per_phase_noise_not_bounded')}
foreach($r in $a){ if([double]$r.summary.vm_working_set_median -gt 256MB){$warnings.Add("native_run_has_vm_processes:$($r.label)")} }
foreach($r in $b){ if([double]$r.summary.vm_process_count_median -lt 1){$warnings.Add("vm_run_without_vm_process:$($r.label)")} }
foreach($r in $b){ if([double]$r.summary.apps_process_count_median -gt 0){$warnings.Add("vm_run_has_host_app_processes:$($r.label)")} }
$keys='in_use_bytes_median','committed_bytes_median','pool_paged_bytes_median','memory_compression_working_set_median','apps_working_set_median','vm_working_set_median'
$m=[ordered]@{}
foreach($k in $keys){
    $na=Get-Median @($a|ForEach-Object{[double]$_.summary.$k});$vb=Get-Median @($b|ForEach-Object{[double]$_.summary.$k})
    $m[$k]=[ordered]@{native=$na;vm=$vb;vm_minus_native=$vb-$na}
}
# Positive = the VM path uses MORE host RAM than running the apps natively under Windows' own compression.
$cost=[double]$m['in_use_bytes_median'].vm_minus_native
$spread=@($a+$b|ForEach-Object{[double]$_.summary.in_use_bytes_max-[double]$_.summary.in_use_bytes_median}|Measure-Object -Maximum).Maximum
if([math]::Abs($cost) -le $spread){$verdict='inconclusive_difference_within_noise'}
elseif($cost -gt 0){$verdict='vm_path_costs_more_ram'}
else{$verdict='vm_path_uses_less_ram'}
$saved=if($verdict -eq 'vm_path_uses_less_ram' -and $warnings.Count -eq 0){[int64](-$cost)}else{$null}
$result=[ordered]@{schema='elasticmem.gate0.compare.v1';native_runs=$a.Count;vm_runs=$b.Count;metrics=$m;noise_bound_bytes=$spread;
    verdict=$verdict;physical_ram_saved_bytes=$saved;warnings=$warnings;
    meaning=switch($verdict){
        'vm_path_costs_more_ram'{"Running the apps in a VM costs $([math]::Round($cost/1MB)) MiB more host RAM than native. A custom pager must recover at least this much beyond what Windows Memory Compression already does natively, before any saving exists."}
        'vm_path_uses_less_ram'{"VM path used $([math]::Round(-$cost/1MB)) MiB less host RAM (in_use median). Counted as saving only when there are no warnings."}
        default{'Difference is within the per-run spread; repeat with more interleaved runs.'}
    }}
$json=$result|ConvertTo-Json -Depth 6
if($Out){$json|Set-Content -Encoding UTF8 -LiteralPath $Out}
$json
