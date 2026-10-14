#!/usr/bin/env python3
"""Changed SYS source + Windows app/client foundation. No fabricated SYS binary."""
import argparse,hashlib,json,pathlib,subprocess,zipfile
p=argparse.ArgumentParser();p.add_argument('--app',required=True);p.add_argument('--client',required=True);p.add_argument('--qa',required=True);p.add_argument('--runner',required=True);p.add_argument('--output',required=True);a=p.parse_args()
r=pathlib.Path(__file__).resolve().parents[1];app=pathlib.Path(a.app).resolve();client=pathlib.Path(a.client).resolve();runner=pathlib.Path(a.runner).resolve();qa=pathlib.Path(a.qa).resolve();out=pathlib.Path(a.output).resolve()
def git(*x):return subprocess.check_output(['git','-C',str(r),*x],text=True).strip()
def sha(f):return hashlib.sha256(f.read_bytes()).hexdigest()
assert not git('status','--porcelain'),'Commit source first';commit=git('rev-parse','HEAD')
from release_inventory import verify_build
build=json.loads((app/'BUILD.json').read_text());verify_build(app,build);assert build['version']=='0.31.0' and build['code_commit']==commit
assert 'WHP FOUNDATION 23 ' in (qa/'core-tests-final.log').read_text();assert '0 Error(s)' in (qa/'vmm-build.log').read_text();assert 'ALL TESTS PASSED' in (qa/'core-tests-final.log').read_text();assert 'VMM PROVIDER 20 ' in (qa/'core-tests-final.log').read_text();assert 'VM BACKENDS 20 ' in (qa/'core-tests-final.log').read_text();assert (qa/'powershell-parse.log').read_text().count('PARSE PASS')==5
assert 'PASS ' in (qa/'store-tests.log').read_text() and 'NOT executed' in (qa/'store-tests.log').read_text()
assert '0 Error(s)' in (qa/'app-build-final.log').read_text() and '0 Error(s)' in (qa/'client-build.log').read_text()
assert git('diff','f3cea87dbe3b5dad9d272aff5154b1f6dcdc79a8','HEAD','--','driver/kmdf/driver.c'),'SYS source was not changed'
exe=(app/'ElasticMem.exe').read_bytes();proof={}
for label,path in {'App':r/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':r/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll','Client':r/'driver/client/bin/Release/net8.0/win-x64/ElasticMem.DriverClient.dll','Vmm':r/'src/ElasticMem.Vmm/bin/Release/net8.0/ElasticMem.Vmm.dll','Runner':r/'tools/ElasticMem.VmmRunner/bin/Release/net8.0/win-x64/ElasticMem.VmmRunner.dll'}.items():
 b=path.read_bytes();target=(runner/'ElasticMem.VmmRunner.exe').read_bytes() if label in ('Vmm','Runner') else exe if label!='Client' else (client/'ElasticMem.DriverClient.exe').read_bytes();offset=target.find(b);assert offset>=0;proof[label]={'sha256':hashlib.sha256(b).hexdigest(),'embedded_offset':offset,'bytes':len(b)}
out.mkdir(parents=True,exist_ok=False)
meta={'schema':'elasticmem.whp.sys.synthetic.delivery.v1','ready_criterion_met':False,'native_synthetic_whp_executed':False,'windows_sdk_abi_probe_executed':False,'portable_whp_foundation_checks':23,'vcpu_limit':1,'io_dma_workers_supported':False,'firmware_devices_windows_boot_implemented':False,'migration_implemented':False,'requires_windows_hypervisor_for_whp':True,'driver_changed_since_030':False,'version':'0.31.0','code_commit':commit,'status':'RESEARCH_FOUNDATION_NOT_PRODUCTION','driver_source_changed':True,'new_sys_built':False,'new_sys_native_execution':False,'native_owned_pager_smoke_executed':False,'custom_vmm_windows_boot_connected':False,'whp_mapping_executed':False,'sys_path_raw_fallback_allowed':False,'portable_vmm_model_checks':20,'portable_vm_type_catalog_checks':20,'per_profile_backend_types':{'1':'custom WHP + SYS synthetic guest; Windows boot not implemented','2':'Hyper-V'},'automatic_hyperv_fallback':False,'sys_vm_runtime_complete':False,'new_driver_protocol':'capabilities + bounded empty-store resize + compressed-only generations','sys_logical_key_capacity_max':1048576,'sys_session_requested_allocation_quota_max_bytes':33554432,'sys_global_requested_allocation_limit_bytes':67108864,'portable_backend_checks':14,'sample_fixture_checks':16,'research_selftests':73217,'c_core_sanitizers':'ASan+UBSan Linux, test codec not XPRESS; includes per-byte wipe assertions','app_client_build':'Windows x64 cross-target only','historical_driver_reports_do_not_qualify_this_revision':True,'windows11_hvci_verifier_sustained_stress_tested':False,'real_edge_chrome_telegram_compression_proven':False,'physical_ram_saved_bytes':None,'assembly_proof':proof,'driver_source_files':{n:sha(r/n) for n in ['driver/shared/elasticmem_ioctl.h','driver/core/em_store.h','driver/core/em_store.c','driver/kmdf/driver.c']}}
(out/'QA.json').write_text(json.dumps(meta,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(r),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().rstrip('\0').split('\0');logs=['vmm-build.log','whp-linux-probe.json','whp-linux-negative.json','whp-header-sha256.txt','core-tests-final.log','store-tests.log','app-build-final.log','client-build.log','powershell-parse.log']
with zipfile.ZipFile(out/'ElasticMem-v0.31.0-WHP-SYS-synthetic-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(r/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa031/'+name)
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead backend/VMM-031.md before WDK build/test. Previous driver/verification is historical. No new SYS binary in this archive.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.31.0-win-x64-WHP-SYS-synthetic-preview.zip','w',zipfile.ZIP_DEFLATED) as z:
 for f in app.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/'+f.relative_to(app).as_posix())
 for f in client.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/driver-client/'+f.relative_to(client).as_posix())
 for f in runner.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/vmm-runner/'+f.relative_to(runner).as_posix())
 for name in tracked:
  if name.startswith('backend/') or (name.startswith('driver/') and not name.startswith('driver/verification/')):z.write(r/name,'ElasticMem/'+name)
 z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa031/'+name)
 z.writestr('ElasticMem/ОТКРЫТЬ-BACKEND.cmd','@echo off\r\nsetlocal DisableDelayedExpansion\r\nstart "" "%~dp0ElasticMem.exe" --backend-ui\r\n')
 z.writestr('ElasticMem/НАЧНИТЕ-ЗДЕСЬ.txt','0.31: НЕ готовый Windows VMM. Новый WHP/SYS synthetic run-loop + native runner в vmm-runner. Здесь не исполнен в Windows. Windows boot/UEFI/devices/multi-vCPU/migration не реализованы. НЕТ end-to-end Edge/Chrome/Telegram и доказанной экономии RAM. WHP требует гипервизор Windows; Hyper-V cmdlets/fallback не используются своим runner. Новый SYS отсутствует: нужен утверждённый WDK/sign/lab путь из исходников0.30. Не использовать kernel-cross/UNSIGNED, не отключать безопасность. Не основной ПК. Читайте backend/VMM-031.md. Запустите ОТКРЫТЬ-BACKEND.cmd; тип1 synthetic кнопки отдельно от существующего типа2 Hyper-V.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any(n.lower().endswith(('.sys','.pfx','.cer','.clixml','.vhd','.vhdx','.qcow2','.dmp')) for n in z.namelist())
(out/'ElasticMem-0.31-WHP-SYS-инструкция-отчёт.md').write_bytes((r/'backend/VMM-031.md').read_bytes())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in out.iterdir() if f.is_file()]},indent=2))
