#!/usr/bin/env python3
"""Offline-only app 0.25 candidate; includes exact previously VM-tested native SYS."""
import argparse,hashlib,json,pathlib,subprocess,zipfile,struct
p=argparse.ArgumentParser();p.add_argument('--app',required=True);p.add_argument('--qa',required=True);p.add_argument('--native-sys',required=True);p.add_argument('--client',required=True);p.add_argument('--output',required=True);a=p.parse_args()
r=pathlib.Path(__file__).resolve().parents[1];app=pathlib.Path(a.app).resolve();qa=pathlib.Path(a.qa).resolve();sys=pathlib.Path(a.native_sys).resolve();client=pathlib.Path(a.client).resolve();out=pathlib.Path(a.output).resolve()
def git(*args):return subprocess.check_output(['git','-C',str(r),*args],text=True).strip()
def sha(f):return hashlib.sha256(f.read_bytes()).hexdigest()
assert not git('status','--porcelain'),'Dirty source';commit=git('rev-parse','HEAD')
from release_inventory import verify_build
build=json.loads((app/'BUILD.json').read_text());verify_build(app,build)
assert build['code_commit']==commit and build['version']=='0.25.0'
assert sha(sys)=='5362244d0d4d0724312ee3e403303190448fbdaf108b3d2a4a3a4b2cd6cea8a5'
assert sha(client)=='d4465ff2c2d0d88a91e6879bca0a0cc79380fc6202c3485bab6aa33b34be3693'
data=sys.read_bytes();pe=struct.unpack_from('<I',data,0x3c)[0];assert struct.unpack_from('<II',data,pe+24+112+8*4)==(0,0)
assert 'ALL TESTS PASSED' in (qa/'core-tests.log').read_text()
assert 'SAMPLE BENCH 16 checks' in (qa/'core-tests.log').read_text()
assert 'PASS 578' in (qa/'transport-tests.log').read_text()
# Confirm updated managed assemblies occur within the exact published bundle.
exe=(app/'ElasticMem.exe').read_bytes();proof={}
for name,path in {'App':r/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':r/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll'}.items():
 b=path.read_bytes();offset=exe.find(b);assert offset>=0;proof[name]={'sha256':hashlib.sha256(b).hexdigest(),'bytes':len(b),'embedded_offset':offset}
out.mkdir(parents=True,exist_ok=False)
metadata={'schema':'elasticmem.research.app.candidate.v1','version':'0.25.0','code_commit':commit,'status':'LOCAL_RESEARCH_NOT_PUBLISHED','app_windows_execution':False,'real_edge_chrome_telegram_test_executed_here':False,'new_kernel_sample_path_native_execution':False,'portable_core_tests':'ALL TESTS PASSED','sample_bench_checks':16,'sample_bench_backend':'test_double_not_native_XPRESS','research_selftests':73217,'transport_checks':578,'embedded_assembly_proof':proof,'driver_binary_changed':False,'driver_tested_code_commit':'2ea62910a9a44fe2f8d25d1fca37f18601abbc1c','driver_native_server2025_smoke_checks':4042,'driver_normal_unload_reload':True,'driver_restricted_token_acl_passed':True,'independent_standard_user_tested':False,'unsigned_native_sys_sha256':sha(sys),'driver_client_sha256':sha(client),'driver_msbuild_executed':False,'windows11_tested':False,'hvci_tested':False,'driver_verifier_tested':False,'new_powershell_wrappers_windows_tested':False,'transparent_foreign_memory_supported':False,'measured_physical_ram_saved_bytes':None}
(out/'QA.json').write_text(json.dumps(metadata,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(r),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(r),'ls-files','-z']).decode().rstrip('\0').split('\0')
with zipfile.ZipFile(out/'ElasticMem-v0.25.0-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(r/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 for f in qa.iterdir():
  if f.is_file() and f.name in {'core-tests.log','transport-tests.log','app-build.log'}:z.write(f,'ElasticMem/qa/'+f.name)
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead windows-check/ПОЛНАЯ-ИНСТРУКЦИЯ.md and docs/update-025.md. Research candidate, not Windows-qualified app release.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.25.0-win-x64-research.zip','w',zipfile.ZIP_DEFLATED) as z:
 for f in app.rglob('*'):
  if f.is_file():z.write(f,'ElasticMem/'+f.relative_to(app).as_posix())
 z.write(out/'QA.json','ElasticMem/QA.json');z.write(sys,'ElasticMem/driver-native-UNSIGNED/ElasticMemStore.sys');z.write(client,'ElasticMem/driver-client/ElasticMem.DriverClient.exe')
 for name in tracked:
  if name.startswith('driver/') or name=='docs/update-025.md':z.write(r/name,'ElasticMem/'+name)
 z.writestr('ElasticMem/START-HERE.txt','0.25.0 local research update, not publicly published. Read ПОЛНАЯ-ИНСТРУКЦИЯ.md first. For ordinary real-app codec sample test no driver is needed. Optional kernel sample mode ONLY in disposable VM after approved local trust/signing; SYS is UNSIGNED. New Windows app path is not executed here. Original target RAM is never released; no transparent compression/physical RAM benefit claim. Do not use retired LLD SYS. Raw captured memory stays local; send logs-only ZIP, not source snapshots/containers.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any('kernel-cross' in n or n.endswith(('.pfx','.cer','signed-native.sys')) for n in z.namelist())
(out/'ElasticMem-0.25.0-полная-инструкция.md').write_bytes((r/'windows-check/ПОЛНАЯ-ИНСТРУКЦИЯ.md').read_bytes())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in out.iterdir() if f.is_file()]},indent=2))
