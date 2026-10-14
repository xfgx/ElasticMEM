#!/usr/bin/env python3
"""Package the exact Server 2025 research-tested native image; never publishes."""
import argparse, hashlib, json, pathlib, struct, subprocess, zipfile
p=argparse.ArgumentParser()
p.add_argument('--evidence',required=True)
p.add_argument('--client',required=True)
p.add_argument('--output',required=True)
a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1]
ev=pathlib.Path(a.evidence).resolve(); client=pathlib.Path(a.client).resolve(); out=pathlib.Path(a.output).resolve()
def git(*args):return subprocess.check_output(['git','-C',str(root),*args],text=True).strip()
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
if git('status','--porcelain'):raise SystemExit('Commit source changes before packaging.')
provenance=json.loads((root/'driver/verification/provenance.json').read_text())
sys=ev/'ElasticMemStore.sys'
if sha(sys)!=provenance['unsigned_native_sys_sha256']:raise SystemExit('Not the exact verified unsigned native image.')
if sha(client)!=provenance['client_sha256']:raise SystemExit('Client differs from tested binary.')
data=sys.read_bytes();pe=struct.unpack_from('<I',data,0x3c)[0];opt=pe+24
assert data[pe:pe+4]==b'PE\0\0' and struct.unpack_from('<H',data,pe+4)[0]==0x8664
assert struct.unpack_from('<H',data,pe+22)[0]&0x2000 # DLL
assert struct.unpack_from('<H',data,opt+68)[0]==1 # Native
assert struct.unpack_from('<II',data,opt+112+8*4)==(0,0) # no signature
smoke=json.loads((root/'driver/verification/kernel-smoke.json').read_text())
assert smoke['checks']==4042 and smoke['status']=='passed_owned_driver_smoke_only'
assert all(row['full_bytes_verified'] for row in smoke['rows'])
acl=json.loads((root/'driver/verification/acl-restricted.json').read_text())
assert acl['status']=='passed' and acl['restricted_win32_error']==5
out.mkdir(parents=True,exist_ok=False)
commit=git('rev-parse','HEAD')
metadata={**provenance,'package_commit':commit,'status':'SERVER2025_RESEARCH_SMOKE_PASSED_NOT_PRODUCTION','checks':4042,'iterations_per_mode':1000,'normal_unload_reload_passed':True,'restricted_token_acl_passed':True,'independent_standard_user_tested':False,'delivered_sys_signed':False,'transparent_foreign_memory_supported':False,'measured_physical_ram_saved_bytes':None}
(out/'QA.json').write_text(json.dumps(metadata,indent=2)+'\n')
bundle=out/'ElasticMem.git.bundle'
subprocess.run(['git','-C',str(root),'bundle','create',str(bundle),'--all'],check=True)
tracked=subprocess.check_output(['git','-C',str(root),'ls-files','-z']).decode().rstrip('\0').split('\0')
with zipfile.ZipFile(out/'ElasticMem-v0.24.0-native-verified-source.zip','w',zipfile.ZIP_DEFLATED) as z:
 for name in tracked:z.write(root/name,'ElasticMem/'+name)
 z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
 z.writestr('ElasticMem/RESTORE.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead driver/verification/REPORT.md. Native Server 2025 research smoke only; not production.\n')
with zipfile.ZipFile(out/'ElasticMem-v0.24.0-driver-native-research.zip','w',zipfile.ZIP_DEFLATED) as z:
 z.write(sys,'ElasticMem/driver-native-UNSIGNED/ElasticMemStore.sys')
 z.write(client,'ElasticMem/driver-client/ElasticMem.DriverClient.exe')
 z.write(out/'QA.json','ElasticMem/QA.json')
 for name in tracked:
  if name.startswith('driver/') or name=='windows-check/THIRD-PARTY-NOTICES.txt':z.write(root/name,'ElasticMem/'+name)
 z.writestr('ElasticMem/START-HERE.txt','RESEARCH ONLY. Exact Microsoft-native-linked SYS passed Server 2025 VM smoke (4042 checks), normal unload/reload and restricted-token ACL. Windows 11/HVCI/Verifier/HLK/MSBuild not tested. Delivered SYS is UNSIGNED; Install-SignedDriver refuses it until locally signed/trusted through your approved process. Disposable VM only; do not disable security protections. No private key, test certificate, MSVC tools or legacy LLD SYS included. Read driver/README.md and driver/verification/REPORT.md. Explicit owned-page store, not transparent Chrome/Telegram RAM optimization.\n')
for f in out.glob('*.zip'):
 with zipfile.ZipFile(f) as z:
  assert z.testzip() is None
  assert not any('kernel-cross' in n or n.endswith(('signed-native.sys','.cer','.pfx')) for n in z.namelist())
(out/'SHA256.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(out.iterdir()) if f.is_file() and f.name!='SHA256.txt'))
print(json.dumps({'commit':commit,'files':[{'name':f.name,'bytes':f.stat().st_size} for f in sorted(out.iterdir()) if f.is_file()]},indent=2))
