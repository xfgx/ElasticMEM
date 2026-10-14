#!/usr/bin/env python3
"""Research VM-creation app preview. No included OS/disk/credential/SYS."""
import argparse,hashlib,json,pathlib,subprocess,zipfile
p=argparse.ArgumentParser();p.add_argument('--app',required=True);p.add_argument('--qa',required=True);p.add_argument('--output',required=True);a=p.parse_args()
r=pathlib.Path(__file__).resolve().parents[1];app=pathlib.Path(a.app).resolve();qa=pathlib.Path(a.qa).resolve();out=pathlib.Path(a.output).resolve()
def git(*x):return subprocess.check_output(['git','-C',str(r),*x],text=True).strip()
def sha(f):return hashlib.sha256(f.read_bytes()).hexdigest()
assert not git('status','--porcelain'),'Commit first';commit=git('rev-parse','HEAD')
from release_inventory import verify_build
build=json.loads((app/'BUILD.json').read_text());verify_build(app,build);assert build['version']=='0.29.0' and build['code_commit']==commit
checks=(qa/'core-tests-final.log').read_text();assert 'ALL TESTS PASSED' in checks and 'VM CREATION 38 ' in checks
assert (qa/'powershell-parse-final.log').read_text().count('PARSE PASS')==5
assert '0 Error(s)' in (qa/'app-build-final.log').read_text()
assert not git('diff','3dcd238e99a4a149f2e28e5fa3dffe9c5f6b8d74','HEAD','--','driver/'),'Unexpected driver change'
exe=(app/'ElasticMem.exe').read_bytes();proof={}
for name,path in {'App':r/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':r/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll'}.items():
 b=path.read_bytes();offset=exe.find(b);assert offset>=0;proof[name]={'sha256':hashlib.sha256(b).hexdigest(),'embedded_offset':offset,'bytes':len(b)}
meta={'schema':'elasticmem.vm.creation.preview.v1','version':'0.29.0','code_commit':commit,'status':'RESEARCH_PREVIEW_NOT_PRODUCTION','native_hyperv_vm_creation_executed':False,'native_secureboot_vtpm_execution':False,'native_windows_gui_execution':False,'windows_guest_boot_tested':False,'portable_creation_contract_checks':38,'portable_backend_checks':14,'portable_vmm_model_checks':20,'sample_fixture_checks':16,'research_selftests':73217,'powershell_check':'5 scripts, Linux PowerShell7.4.13 syntax Parser only','existing_windows_vhdx_required_for_prepared_os':True,'iso_unattended_windows_install_implemented':False,'rds_domain_licensing_automatically_configured':False,'sys_driver_automatically_installed':False,'new_sys_built':False,'custom_sys_vm_pager_complete':False,'driver_changed_since_028':False,'guest_credentials_scoped_by_vm_guid':True,'physical_ram_saved_bytes':None,'assembly_proof':proof}
out.mkdir(parents=True,exist_ok=False);(out/'QA.json').write_text(json.dumps(meta,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(r),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().rstrip('\0').split('\0');logs=['core-tests-final.log','powershell-parse-final.log','app-build-final.log']
with zipfile.ZipFile(out/'ElasticMem-v0.29.0-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(r/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa029/'+name)
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead backend/AUTO-VM-029.md. VM creation is not native Windows tested. No included OS/SYS or complete SYS-VMM.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.29.0-win-x64-auto-VM-preview.zip','w',zipfile.ZIP_DEFLATED) as z:
 for f in app.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/'+f.relative_to(app).as_posix())
 for name in tracked:
  if name.startswith('backend/'):z.write(r/name,'ElasticMem/'+name)
 z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa029/'+name)
 z.writestr('ElasticMem/ОТКРЫТЬ-BACKEND.cmd','@echo off\r\nsetlocal DisableDelayedExpansion\r\nstart "" "%~dp0ElasticMem.exe" --backend-ui\r\n')
 z.writestr('ElasticMem/НАЧНИТЕ-ЗДЕСЬ.txt','0.29 исследовательский Windows preview. Прочитайте backend/AUTO-VM-029.md. Запустите ElasticMem.exe от администратора → Backend VM → Создать Windows VM автоматически. Нужен Hyper-V и ваш доверенный Windows VHDX (готовая ОС) либо ISO (требует Windows Setup). RDS/лицензии/приложения/новый SYS автоматически не ставятся. Hyper-V VM не является законченным custom SYS-пейджером. Native creation/vTPM/guest boot здесь не проверены. QA.json содержит границы проверки.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any(n.lower().endswith(('.sys','.pfx','.cer','.clixml','.iso','.vhd','.vhdx','.qcow2','.dmp')) for n in z.namelist())
(out/'ElasticMem-0.29-автосоздание-VM.md').write_bytes((r/'backend/AUTO-VM-029.md').read_bytes())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in out.iterdir() if f.is_file()]},indent=2))
