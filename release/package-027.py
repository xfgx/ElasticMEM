#!/usr/bin/env python3
"""Offline Windows orchestration preview, explicitly NOT a complete SYS pager."""
import argparse,hashlib,json,pathlib,subprocess,zipfile
p=argparse.ArgumentParser();p.add_argument('--app',required=True);p.add_argument('--qa',required=True);p.add_argument('--output',required=True);a=p.parse_args()
r=pathlib.Path(__file__).resolve().parents[1];app=pathlib.Path(a.app).resolve();qa=pathlib.Path(a.qa).resolve();out=pathlib.Path(a.output).resolve()
def git(*args):return subprocess.check_output(['git','-C',str(r),*args],text=True).strip()
def sha(f):return hashlib.sha256(f.read_bytes()).hexdigest()
assert not git('status','--porcelain'),'Dirty source';commit=git('rev-parse','HEAD')
from release_inventory import verify_build
build=json.loads((app/'BUILD.json').read_text());verify_build(app,build)
assert build['code_commit']==commit and build['version']=='0.27.0'
checks=(qa/'core-tests-verified.log').read_text();assert 'ALL TESTS PASSED' in checks
assert 'PASS 578' in (qa/'transport-tests.log').read_text()
assert (qa/'powershell-parse-final.log').read_text().count('PARSE PASS')==4
exe=(app/'ElasticMem.exe').read_bytes();proof={}
for name,path in {'App':r/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':r/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll'}.items():
 b=path.read_bytes();offset=exe.find(b);assert offset>=0;proof[name]={'sha256':hashlib.sha256(b).hexdigest(),'bytes':len(b),'embedded_offset':offset}
assert not git('diff','96bf46f342429c8648d01e6fb0a483007eab6632','HEAD','--','driver/'),'Driver source unexpectedly changed'
out.mkdir(parents=True,exist_ok=False)
metadata={'schema':'elasticmem.backend.preview.v1','version':'0.27.0','code_commit':commit,'status':'RESEARCH_PREVIEW_NOT_PRODUCTION','windows_backend_end_to_end_executed':False,'custom_sys_pager_complete':False,'custom_vmm_connected':False,'new_sys_built':False,'driver_source_changed':False,'native_whp_provider_executed':False,'runtime_memory_backend':'guest Windows Memory Compression plus Hyper-V Dynamic Memory, NOT custom SYS','portable_backend_contract_checks':14,'vmm_test_double_lifecycle_checks':13,'sample_bench_checks':16,'research_selftests':73217,'transport_checks':578,'powershell_syntax_only':'Linux PowerShell 7.4.13 Parser, 4 scripts; not Windows 5.1 execution','windows11_tested':False,'hvci_tested':False,'driver_verifier_tested':False,'real_edge_chrome_telegram_test_executed_here':False,'measured_physical_ram_saved_bytes':None,'embedded_assembly_proof':proof}
(out/'QA.json').write_text(json.dumps(metadata,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(r),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().rstrip('\0').split('\0')
logs=['core-tests-verified.log','transport-tests.log','powershell-parse-final.log']
with zipfile.ZipFile(out/'ElasticMem-v0.27.0-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(r/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa/'+name)
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead backend/VERIFICATION.md and backend/УСТАНОВКА-WINDOWS.md. No new SYS, no complete Windows-bootable custom VMM.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.27.0-win-x64-preview.zip','w',zipfile.ZIP_DEFLATED) as z:
 for f in app.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/'+f.relative_to(app).as_posix())
 for name in tracked:
  if name.startswith('backend/'):z.write(r/name,'ElasticMem/'+name)
 z.write(out/'QA.json','ElasticMem/QA.json')
 for name in logs:z.write(qa/name,'ElasticMem/qa/'+name)
 z.writestr('ElasticMem/НАЧНИТЕ-ЗДЕСЬ.txt','0.27.0 — исследовательский preview оркестратора Windows. Сначала прочитайте backend/УСТАНОВКА-WINDOWS.md и QA.json. Запуск: ElasticMem.exe --backend-ui от администратора. Нужны Hyper-V и уже настроенная Windows Server/RDS RemoteApp среда с разрешёнными лицензиями/политиками. Наш SYS не используется для сжатия памяти VM; нового SYS в пакете нет. WHP/SYS memory-provider есть только в исходниках, не подключён к полноценному VMM. Windows end-to-end, HVCI, Verifier и физическая экономия RAM не проверены. Не production. Не отправляйте credentials, дампы памяти или диски VM.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any(n.lower().endswith(('.pfx','.cer','.clixml','.sys','.vhd','.vhdx','.qcow2','.dmp')) for n in z.namelist()),'Private/native artifact unexpectedly included'
(out/'ElasticMem-0.27.0-установка-Windows.md').write_bytes((r/'backend/УСТАНОВКА-WINDOWS.md').read_bytes())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in out.iterdir() if f.is_file()]},indent=2))
