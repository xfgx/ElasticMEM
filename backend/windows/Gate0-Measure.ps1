# Gate 0: read-only host-wide memory measurement for the A/B "is there anything to save?" question.
# Phase native: Edge/Chrome/Telegram run directly on the host. Phase vm: the SAME scenario inside a VM
# (Windows Sandbox / Hyper-V / own VMM). The script only samples counters: it starts, installs, enables,
# trims or changes nothing, does not need elevation, and records process NAMES only (no command lines,
# titles, URLs, tokens or memory contents).
param(
    [Parameter(Mandatory=$true)][ValidateSet('native','vm')][string]$Phase,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$Label,
    [ValidateRange(1,600)][int]$DurationMinutes=15,
    [ValidateRange(1,600)][int]$WarmupMinutes=3,
    [ValidateRange(1,300)][int]$IntervalSeconds=5,
    [string]$OutDir=(Join-Path $env:LOCALAPPDATA 'ElasticMem\gate0')
)
$ErrorActionPreference='Stop'
if($WarmupMinutes -ge $DurationMinutes){throw 'WarmupMinutes must be smaller than DurationMinutes'}
$groups=[ordered]@{
    apps=@('msedge','chrome','Telegram');
    vm=@('vmmem*','vmwp','vmcompute','WindowsSandbox*','qemu-system-*','ElasticMem*','mstsc','msrdc');
    memory_compression=@('Memory Compression')
}
function Get-GroupSample($patterns){
    $ws=[uint64]0;$priv=[uint64]0;$n=0
    foreach($p in Get-Process -ErrorAction SilentlyContinue){
        foreach($pat in $patterns){ if($p.ProcessName -like $pat){ $ws+=[uint64]$p.WorkingSet64; $priv+=[uint64]$p.PrivateMemorySize64; $n++; break } }
    }
    [ordered]@{count=$n;working_set_bytes=$ws;private_bytes=$priv}
}
function Get-Sample{
    $os=Get-CimInstance Win32_OperatingSystem
    $m=Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $total=[uint64]$os.TotalVisibleMemorySize*1024
    $s=[ordered]@{
        t=(Get-Date).ToUniversalTime().ToString('o');
        total_physical_bytes=$total;
        available_bytes=[uint64]$m.AvailableBytes;
        in_use_bytes=$total-[uint64]$m.AvailableBytes;
        committed_bytes=[uint64]$m.CommittedBytes;
        pool_paged_bytes=[uint64]$m.PoolPagedBytes;
        pool_nonpaged_bytes=[uint64]$m.PoolNonpagedBytes;
        modified_list_bytes=[uint64]$m.ModifiedPageListBytes;
        standby_bytes=[uint64]$m.StandbyCacheCoreBytes+[uint64]$m.StandbyCacheNormalPriorityBytes+[uint64]$m.StandbyCacheReserveBytes
    }
    foreach($k in $groups.Keys){$s[$k]=Get-GroupSample $groups[$k]}
    $s
}
function Get-Median([double[]]$x){ if(-not $x -or $x.Count -eq 0){return $null}; $y=$x|Sort-Object; $c=$y.Count; if($c%2){return [double]$y[($c-1)/2]}; return ([double]$y[$c/2-1]+[double]$y[$c/2])/2 }

$info=[ordered]@{os_build=[Environment]::OSVersion.Version.ToString();is_windows11=([Environment]::OSVersion.Version.Build -ge 22000)}
try{$dg=Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard;$info.vbs_status=[int]$dg.VirtualizationBasedSecurityStatus;$info.hvci_running=([int[]]$dg.SecurityServicesRunning -contains 2)}catch{$info.vbs_status=$null;$info.hvci_running=$null}
try{$mm=Get-MMAgent;$info.memory_compression=[bool]$mm.MemoryCompression;$info.page_combining=[bool]$mm.PageCombining}catch{$info.memory_compression=$null;$info.page_combining=$null}
try{$info.hypervisor_present=[bool](Get-CimInstance Win32_ComputerSystem).HypervisorPresent}catch{$info.hypervisor_present=$null}

New-Item -ItemType Directory -Force -Path $OutDir|Out-Null
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$base=Join-Path $OutDir "gate0-$Phase-$Label-$stamp"
$samples=New-Object System.Collections.Generic.List[object]
$end=(Get-Date).AddMinutes($DurationMinutes);$warmEnd=(Get-Date).AddMinutes($WarmupMinutes)
Write-Host "Gate0 ${Phase}/${Label}: sampling every $IntervalSeconds s for $DurationMinutes min (first $WarmupMinutes min = warmup, excluded). Do not change the scenario during the run."
while((Get-Date) -lt $end){
    $s=Get-Sample;$s.warmup=((Get-Date) -lt $warmEnd);$samples.Add($s)
    Start-Sleep -Seconds $IntervalSeconds
}
$steady=@($samples|Where-Object{-not $_.warmup})
if($steady.Count -lt 3){throw 'Too few steady-state samples'}
$summary=[ordered]@{}
foreach($k in 'in_use_bytes','available_bytes','committed_bytes','pool_paged_bytes','pool_nonpaged_bytes','modified_list_bytes','standby_bytes'){
    $summary["${k}_median"]=Get-Median @($steady|ForEach-Object{[double]$_[$k]})
    $summary["${k}_max"]=($steady|ForEach-Object{[double]$_[$k]}|Measure-Object -Maximum).Maximum
}
foreach($g in $groups.Keys){
    $summary["${g}_working_set_median"]=Get-Median @($steady|ForEach-Object{[double]$_[$g].working_set_bytes})
    $summary["${g}_private_median"]=Get-Median @($steady|ForEach-Object{[double]$_[$g].private_bytes})
    $summary["${g}_process_count_median"]=Get-Median @($steady|ForEach-Object{[double]$_[$g].count})
}
$result=[ordered]@{schema='elasticmem.gate0.v1';phase=$Phase;label=$Label;environment=$info;interval_seconds=$IntervalSeconds;duration_minutes=$DurationMinutes;warmup_minutes=$WarmupMinutes;
    steady_samples=$steady.Count;summary=$summary;samples=$samples;
    note='Host-wide counters. in_use = total - available (includes the Memory Compression store and kernel pool). physical_ram_saved_bytes is computed only by Gate0-Compare from matched native/vm runs.'}
$result|ConvertTo-Json -Depth 6|Set-Content -Encoding UTF8 -LiteralPath "$base.json"
Write-Host "Saved $base.json  in_use median = $([math]::Round($summary.in_use_bytes_median/1MB)) MiB, committed median = $([math]::Round($summary.committed_bytes_median/1MB)) MiB"
