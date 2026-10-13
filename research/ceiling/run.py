#!/usr/bin/env python3
"""Commit/tag/hash runner. A failed/unsupported run is evidence, not a result."""
import argparse, csv, hashlib, json, math, os, platform, re, shutil, statistics, subprocess, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).parent))
import g0, page_idle

def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as f:
        while b := f.read(1024*1024): h.update(b)
    return h.hexdigest()

def git(*args): return subprocess.check_output(['git','-C',str(ROOT),*args], text=True).strip()

def quantile(values, q):
    values = sorted(values); i = (len(values)-1)*q; lo = math.floor(i); hi = math.ceil(i)
    return values[lo] + (values[hi]-values[lo])*(i-lo)

def summarize_g3(folder, iterations):
    status = json.loads((folder/'status.json').read_text())
    if status.get('status') != 'measured': raise ValueError('Native measurement not completed')
    groups={}; total=0
    with (folder/'samples.csv').open(newline='') as f:
        for row in csv.DictReader(f):
            total+=1
            if row['byte_match'] != '1': raise ValueError('Byte mismatch')
            vals = [float(row[k]) for k in ('total_us','transfer_us','materialize_us')]
            if any(not math.isfinite(x) or x < 0 for x in vals): raise ValueError('Invalid timing')
            if abs(vals[0]-vals[1]-vals[2])>max(.02,vals[0]*.0001): raise ValueError('Timing cross-foot mismatch')
            if row['warmup'] not in ('0','1'): raise ValueError('Invalid warmup flag')
            key=(row['mode'],int(row['bytes'])); it=int(row['iteration'])
            if row['warmup'] != str(int(it < 10)): raise ValueError('Warmup flag does not match iteration')
            group=groups.setdefault(key, {'all_iterations':set(), 'values':[]})
            if it in group['all_iterations']: raise ValueError('Duplicate iteration')
            group['all_iterations'].add(it)
            if row['warmup']=='0': group['values'].append(vals[0])
    expected={(mode,size) for mode in ('gpu_write','gpu_restore','disk_restore') for size in (4096,65536,1048576,16777216)}
    if set(groups)!=expected: raise ValueError('Incomplete benchmark groups')
    summaries=[]
    for (mode,size),group in sorted(groups.items()):
        values=group['values']
        if len(values)!=iterations or group['all_iterations']!=set(range(iterations+10)): raise ValueError('Incomplete iteration coverage')
        summaries.append({'mode':mode,'bytes':size,'n':len(values),'p50_us':statistics.median(values),'p95_us':quantile(values,.95),'p99_us':quantile(values,.99),
                          'min_us':min(values),'max_us':max(values),'median_gib_per_s':size/(statistics.median(values)*1e-6)/(1024**3) if statistics.median(values)>0 else None})
    (folder/'summary.json').write_text(json.dumps({'status':'measured_microbenchmark_only','groups':summaries,'limitations':status['limitations']},indent=2)+'\n')
    return summaries

def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--id',required=True); p.add_argument('--out-root',default=str(Path(__file__).parent/'runs'))
    sub=p.add_subparsers(dest='mode',required=True)
    a=sub.add_parser('g0'); a.add_argument('--trace',required=True); a.add_argument('--metadata',required=True)
    a.add_argument('--eviction-us',type=float,default=10); a.add_argument('--recovery-us',type=float,default=100)
    a.add_argument('--boundary-mode',choices=['interior','all'],default='interior')
    a.add_argument('--mean-budgets-us',default='0,0.1,1,10'); a.add_argument('--max-fault-us',type=float,default=100)
    sub.add_parser('probe')
    sub.add_parser('selftest')
    a=sub.add_parser('idle'); a.add_argument('--pids',nargs='+',type=int,required=True); a.add_argument('--interval',type=float,default=.1)
    a.add_argument('--samples',type=int,default=100); a.add_argument('--max-pages',type=int,default=8192)
    a=sub.add_parser('g3'); a.add_argument('--exe',required=True); a.add_argument('--scratch',required=True)
    a.add_argument('--iterations',type=int,default=100); a.add_argument('--pool-mib',type=int,default=32); a.add_argument('--adapter-index',type=int,default=0)
    a=sub.add_parser('inventory'); a.add_argument('files',nargs='+')
    args=p.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,79}',args.id): p.error('Unsafe run ID')
    if git('status','--porcelain','--untracked-files=all'): p.error('Commit ALL source changes before a run; dirty tree refused')
    commit=git('rev-parse','HEAD'); tag='run/'+args.id
    folder=Path(args.out_root).resolve()/args.id
    if folder.exists(): p.error('Run directory already exists')
    if subprocess.run(['git','-C',str(ROOT),'rev-parse','--verify','refs/tags/'+tag],capture_output=True).returncode==0: p.error('Run tag already exists')
    folder.mkdir(parents=True); (folder/'inputs').mkdir()
    git('tag','-a',tag,'-m','ElasticMem run '+args.id+'; see manifest.json',commit)
    manifest={'schema':'elasticmem.run.v1','id':args.id,'mode':args.mode,'commit':commit,'git_tag':tag,'utc_started':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),
              'platform':platform.platform(),'python':sys.version,'argv':sys.argv,'inputs':[],'status':'started'}
    def stage(path, name):
        src=Path(path).resolve(); before=digest(src); dest=folder/'inputs'/name; shutil.copyfile(src,dest)
        if digest(dest)!=before or digest(src)!=before: raise RuntimeError('Input changed during copy')
        manifest['inputs'].append({'original_path':str(src),'path':dest.relative_to(folder).as_posix(),'bytes':dest.stat().st_size,'sha256':before})
        return dest
    code=1
    try:
        if args.mode=='g0':
            trace=stage(args.trace,'trace.csv'); meta=stage(args.metadata,'metadata.json')
            r=g0.analyze(trace,meta,folder/'raw',args.eviction_us,args.recovery_us,tuple(map(float,args.mean_budgets_us.split(','))),args.max_fault_us,args.boundary_mode)
            manifest['status']=r['status']; code=0
        elif args.mode=='selftest':
            raw=folder/'raw'; raw.mkdir()
            command=[sys.executable,'-m','unittest','discover','-s',str(Path(__file__).parent/'tests'),'-v']
            with (raw/'stdout.txt').open('w') as stdout,(raw/'stderr.txt').open('w') as stderr:
                result=subprocess.run(command,stdout=stdout,stderr=stderr,timeout=180)
            manifest['test_exit_code']=result.returncode
            if result.returncode: raise RuntimeError('Selftests failed')
            manifest['status']='selftests_passed_not_workload_measurement'; code=0
        elif args.mode=='probe':
            r=page_idle.probe(); (folder/'probe.json').write_text(json.dumps(r,indent=2)+'\n'); manifest['status']='capability_probe_only'; code=0
        elif args.mode=='idle':
            page_idle.collect(args.pids,folder/'raw',args.interval,args.samples,args.max_pages); manifest['status']='sampled_observations_only'; code=0
        elif args.mode=='inventory':
            # Do not copy huge dumps into an inventory run; inventory is NOT a self-contained D/E experiment.
            with (folder/'dump_inventory.csv').open('w',newline='') as f:
                writer=csv.writer(f); writer.writerow(['path','bytes','sha256','provenance'])
                for path in args.files:
                    src=Path(path).resolve(); writer.writerow([str(src),src.stat().st_size,digest(src),'preserved snapshot; collection provenance incomplete'])
            manifest['status']='hash_inventory_only_not_experiment'; code=0
        else:
            if platform.system()!='Windows': raise RuntimeError('G3 requires Windows with discrete D3D12 GPU; no synthetic substitute')
            exe=stage(args.exe,'vram_bench.exe'); raw=folder/'raw'; raw.mkdir()
            build_info=Path(args.exe).resolve().parent/'build-info.json'
            if not build_info.exists(): raise RuntimeError('Missing build provenance; run windows/build.ps1 at this commit')
            retained_build=stage(build_info,'build-info.json')
            bi=json.loads(retained_build.read_text(encoding='utf-8-sig'))
            if bi.get('ExitCode') != 0 or bi.get('Commit') != commit or bi.get('SourceSHA256','').lower() != digest(Path(__file__).parent/'windows'/'vram_bench.cpp') or bi.get('ExeSHA256','').lower() != digest(exe):
                raise RuntimeError('Build/source/EXE provenance mismatch; rebuild at current clean commit')
            manifest['build_provenance']='declared_build_manifest_hashes_and_commit_verified_not_signed'
            build_log=Path(args.exe).resolve().parent/'build.log'
            if build_log.exists(): stage(build_log,'build.log')
            hardware_script="""$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new();
$scratch=[IO.Path]::GetFullPath($env:ELASTICMEM_SCRATCH);
$qualifier=Split-Path -Qualifier $scratch;
if($qualifier -notmatch '^[A-Za-z]:$'){throw 'Scratch must be on a local drive'};
$letter=$qualifier.Substring(0,1); $partition=Get-Partition -DriveLetter $letter; $disk=Get-Disk -Number $partition.DiskNumber;
@{OS=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber);
GPU=(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,PNPDeviceID);
ScratchMapping=@{ScratchPath=$scratch;DriveLetter=$letter;DiskNumber=$disk.Number;BusType=$disk.BusType.ToString();Model=$disk.FriendlyName;Size=$disk.Size}} | ConvertTo-Json -Depth 5 -Compress"""
            env=dict(os.environ,ELASTICMEM_SCRATCH=str(Path(args.scratch).resolve()))
            hw=subprocess.run(['powershell.exe','-NoProfile','-Command',hardware_script],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60,env=env)
            (raw/'hardware-inventory.txt').write_text(hw.stdout,encoding='utf-8')
            (raw/'hardware-inventory-stderr.txt').write_text(hw.stderr,encoding='utf-8')
            manifest['hardware_inventory_exit_code']=hw.returncode
            hardware_valid=False
            try:
                inventory=json.loads(hw.stdout.lstrip('\ufeff')); mapping=inventory['ScratchMapping']
                hardware_valid=bool(hw.returncode==0 and inventory.get('OS') and inventory.get('GPU') and mapping.get('Model') and mapping.get('BusType') and
                    os.path.normcase(mapping['ScratchPath'])==os.path.normcase(str(Path(args.scratch).resolve())))
                manifest['scratch_disk_bus_type']=mapping['BusType']
            except (ValueError,KeyError,TypeError): pass
            command=[str(exe),str(raw),str(Path(args.scratch).resolve()),str(args.iterations),str(args.pool_mib),str(args.adapter_index)]
            with (folder/'stdout.txt').open('w') as stdout,(folder/'stderr.txt').open('w') as stderr:
                result=subprocess.run(command,stdout=stdout,stderr=stderr,timeout=1800)
            manifest['native_exit_code']=result.returncode
            if result.returncode==3: manifest['status']='unsupported_no_measurement'; code=3
            elif result.returncode!=0: raise RuntimeError('Native benchmark failed; partial rows cannot be used')
            else:
                summarize_g3(raw,args.iterations)
                manifest['status']='measured_microbenchmark_only' if hardware_valid else 'measured_microbenchmark_incomplete_provenance'
                code=0 if hardware_valid else 4
    except Exception as e:
        manifest['status']='failed_no_valid_result'; manifest['error']=str(e); (folder/'error.txt').write_text(repr(e)+'\n'); code=1
    manifest['utc_finished']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())
    try:
        source_valid=git('rev-parse','HEAD')==commit and not git('status','--porcelain','--untracked-files=all') and git('rev-parse','refs/tags/'+tag+'^{commit}') == commit
    except subprocess.CalledProcessError:
        source_valid=False
    if not source_valid:
        manifest['status']='invalid_source_changed_during_run'; code=1
    report=f"# ElasticMem run {args.id}\n\nStatus: **{manifest['status']}**\n\nCode commit: `{commit}`\n\nGit tag: `{tag}`\n\nPlatform: {manifest['platform']}\n\n"
    if args.mode=='g0': report+='G0: optimum of a defined independent-gap scoring model, NOT a demand-paging upper bound or universal ceiling. See raw/g0.json for assumptions, raw/curve.csv for raw curve. Synthetic inputs cannot justify a go/no-go decision. Costs are parameters, not measured on this host.\n'
    elif args.mode=='g3': report+='G3: GPU_UPLOAD CPU write and fenced GPU DMA to READBACK plus RAM materialization. Disk comparator is unbuffered file I/O, NOT NVMe pagefile. No paging integration and no compression claim. See raw samples and device/status files.\n'
    else: report+='This run records capabilities, sampled observations, or input hashes only; it is not evidence for a compression gain.\n'
    if 'error' in manifest: report+='\nFailure: '+manifest['error']+'\n'
    report+='\nAll retained files except manifest.json are hashed in manifest.json. Input copies are retained for analysis/native runs. Inventory entries require the original dumps separately.\n'
    (folder/'report.md').write_text(report)
    manifest['artifacts']=[{'path':x.relative_to(folder).as_posix(),'bytes':x.stat().st_size,'sha256':digest(x)} for x in sorted(folder.rglob('*')) if x.is_file() and x != folder/'manifest.json']
    (folder/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print(json.dumps({'run':str(folder),'status':manifest['status'],'commit':commit,'tag':tag}))
    return code
if __name__=='__main__': sys.exit(main())
