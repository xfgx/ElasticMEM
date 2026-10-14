#!/usr/bin/env python3
"""Changed SYS source + Windows app/client foundation. No fabricated SYS binary."""
import argparse,hashlib,json,pathlib,subprocess,zipfile
p=argparse.ArgumentParser();p.add_argument('--app',required=True);p.add_argument('--client',required=True);p.add_argument('--qa',required=True);p.add_argument('--output',required=True);a=p.parse_args()
r=pathlib.Path(__file__).resolve().parents[1];app=pathlib.Path(a.app).resolve();client=pathlib.Path(a.client).resolve();qa=pathlib.Path(a.qa).resolve();out=pathlib.Path(a.output).resolve()
def git(*x):return subprocess.check_output(['git','-C',str(r),*x],text=True).strip()
def sha(f):return hashlib.sha256(f.read_bytes()).hexdigest()
assert not git('status','--porcelain'),'Commit source first';commit=git('rev-parse','HEAD')
from release_inventory import verify_build
build=json.loads((app/'BUILD.json').read_text());verify_build(app,build);assert build['version']=='0.30.0' and build['code_commit']==commit
assert 'ALL TESTS PASSED' in (qa/'core-tests-final.log').read_text();assert 'VMM PROVIDER 20 ' in (qa/'core-tests-final.log').read_text();assert 'VM BACKENDS 20 ' in (qa/'core-tests-final.log').read_text();assert (qa/'powershell-parse.log').read_text().count('PARSE PASS')==5
assert 'PASS ' in (qa/'store-tests.log').read_text() and 'NOT executed' in (qa/'store-tests.log').read_text()
assert '0 Error(s)' in (qa/'app-build-final.log').read_text() and '0 Error(s)' in (qa/'client-build.log').read_text()
assert git('diff','f3cea87dbe3b5dad9d272aff5154b1f6dcdc79a8','HEAD','--','driver/kmdf/driver.c'),'SYS source was not changed'
exe=(app/'ElasticMem.exe').read_bytes();proof={}
for label,path in {'App':r/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':r/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll','Client':r/'driver/client/bin/Release/net8.0/win-x64/ElasticMem.DriverClient.dll'}.items():
 b=path.read_bytes();target=exe if label!='Client' else (client/'ElasticMem.DriverClient.exe').read_bytes();offset=target.find(b);assert offset>=0;proof[label]={'sha256':hashlib.sha256(b).hexdigest(),'embedded_offset':offset,'bytes':len(b)}
out.mkdir(parents=True,exist_ok=False)
meta={'schema':'elasticmem.sys.foundation.v1','version':'0.30.0','code_commit':commit,'status':'RESEARCH_FOUNDATION_NOT_PRODUCTION','driver_source_changed':True,'new_sys_built':False,'new_sys_native_execution':False,'native_owned_pager_smoke_executed':False,'custom_vmm_windows_boot_connected':False,'whp_mapping_executed':False,'sys_path_raw_fallback_allowed':False,'portable_vmm_model_checks':20,'portable_vm_type_catalog_checks':20,'per_profile_backend_types':{'1':'SYS store; own Windows VMM not implemented','2':'Hyper-V'},'automatic_hyperv_fallback':False,'sys_vm_runtime_complete':False,'new_driver_protocol':'capabilities + bounded empty-store resize + compressed-only generations','sys_logical_key_capacity_max':1048576,'sys_session_requested_allocation_quota_max_bytes':33554432,'sys_global_requested_allocation_limit_bytes':67108864,'portable_backend_checks':14,'sample_fixture_checks':16,'research_selftests':73217,'c_core_sanitizers':'ASan+UBSan Linux, test codec not XPRESS; includes per-byte wipe assertions','app_client_build':'Windows x64 cross-target only','historical_driver_reports_do_not_qualify_this_revision':True,'windows11_hvci_verifier_sustained_stress_tested':False,'real_edge_chrome_telegram_compression_proven':False,'physical_ram_saved_bytes':None,'assembly_proof':proof,'driver_source_files':{n:sha(r/n) for n in ['driver/shared/elasticmem_ioctl.h','driver/core/em_store.h','driver/core/em_store.c','driver/kmdf/driver.c']}}
(out/'QA.json').write_text(json.dumps(meta,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(r),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().rstrip('\0').split('\0');logs=['core-tests-final.log','store-tests.log','app-build-final.log','client-build.log','powershell-parse.log']
with zipfile.ZipFile(out/'ElasticMem-v0.30.0-SYS-types-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(r/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa030/'+name)
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead backend/SYS-TYPES-030.md before WDK build/test. Previous driver/verification is historical. No new SYS binary in this archive.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.30.0-win-x64-SYS-types-preview.zip','w',zipfile.ZIP_DEFLATED) as z:
 for f in app.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/'+f.relative_to(app).as_posix())
 for f in client.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/driver-client/'+f.relative_to(client).as_posix())
 for name in tracked:
  if name.startswith('backend/') or (name.startswith('driver/') and not name.startswith('driver/verification/')):z.write(r/name,'ElasticMem/'+name)
 z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa030/'+name)
 z.writestr('ElasticMem/ОТКРЫТЬ-BACKEND.cmd','@echo off\r\nsetlocal DisableDelayedExpansion\r\nstart "" "%~dp0ElasticMem.exe" --backend-ui\r\n')
 z.writestr('ElasticMem/НАЧНИТЕ-ЗДЕСЬ.txt','0.30: изменённые исходники SYS + отдельные профили типа1 SYS / типа2 Hyper-V. Тип1 не вызывает Hyper-V, но ещё НЕ умеет запускать Windows-VM. Читайте backend/SYS-TYPES-030.md. Новый SYS в пакете отсутствует: нужен WDK build из отдельного source.zip + разрешённая подпись + одноразовая VM. Старый SYS не поддерживает новый pager-протокол; ошибка до освобождения исходного буфера — ожидаема. SYS-кнопка проверяет ТОЛЬКО свои64 страницы, не Windows guest/Edge/Chrome/Telegram. Нужен НОВЫЙ SYS с capabilities/configure-store; старые бинарники не подходят. Native Windows проверка этой ревизии не выполнена. Не production. Hyper-V backend остаётся отдельным путём, не доказательством нашего SYS-paging.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any(n.lower().endswith(('.sys','.pfx','.cer','.clixml','.vhd','.vhdx','.qcow2','.dmp')) for n in z.namelist())
(out/'ElasticMem-0.30-SYS-типы-инструкция.md').write_bytes((r/'backend/SYS-TYPES-030.md').read_bytes())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in out.iterdir() if f.is_file()]},indent=2))
