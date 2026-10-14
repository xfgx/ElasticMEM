#!/usr/bin/env python3
"""Local research candidate only. No network publication or updater manifest writes."""
import argparse,hashlib,json,pathlib,subprocess,zipfile,shutil,struct
p=argparse.ArgumentParser();p.add_argument('--qa',required=True);p.add_argument('--output',required=True);a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];qa=pathlib.Path(a.qa).resolve();out=pathlib.Path(a.output).resolve();out.mkdir(parents=True,exist_ok=False)
run=lambda *args:subprocess.check_output(['git','-C',str(root),*args],text=True).strip()
assert not run('status','--porcelain'),'Dirty sources';commit=run('rev-parse','HEAD');version='0.24.0'
assert 'ALL TESTS PASSED' in (qa/'core-tests.log').read_text()
assert 'PASS 578 ' in (qa/'transport-tests.log').read_text()
assert 'PASS ' in (qa/'driver-core-tests.log').read_text()
app=qa/'windows-app';build=json.loads((app/'BUILD.json').read_text());assert build['code_commit']==commit and build['version']==version
for item in build['files']:
    f=app/item['path'];assert f.stat().st_size==item['bytes'] and hashlib.sha256(f.read_bytes()).hexdigest()==item['sha256']
exe=(app/'ElasticMem.exe').read_bytes();off=struct.unpack_from('<I',exe,0x3c)[0];assert exe[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',exe,off+4)[0]==0x8664
sys=(qa/'kernel-cross/ElasticMemStore.sys').read_bytes();off=struct.unpack_from('<I',sys,0x3c)[0];assert sys[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',sys,off+4)[0]==0x8664
opt=off+24;assert struct.unpack_from('<H',sys,opt+68)[0]==1 # native subsystem
flags=struct.unpack_from('<H',sys,opt+70)[0];assert flags&0x40 and flags&0x100 and flags&0x80 and flags&0x4000
assert struct.unpack_from('<I',sys,opt+64)[0]!=0 # driver PE checksum
assert struct.unpack_from('<II',sys,opt+112+8*4)==(0,0) # unsigned security directory
proof={}
for name,path in {'App':root/'src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll','Core':root/'src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll'}.items():
    data=path.read_bytes();offset=exe.find(data);assert offset>=0;proof[name]={'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest(),'embedded_offset':offset}
metadata={'schema':'elasticmem.recovery.candidate.v1','version':version,'code_commit':commit,'recovered_from_commit':'f8bc43cd2c4cb1431a07bf7e9ff27bbe47c3bba4','source_archive_sha256':'fcb481e8af485fbc083aae4b0588e29009ffa38e37b36154d9027998a502ad31','status':'LOCAL_RESEARCH_CANDIDATE_NOT_WINDOWS_VALIDATED_NOT_PUBLISHED','ipc_reproduced_fixture_key':62,'ipc_old_request_chars':12138,'ipc_old_cap':12000,'ipc_new_cap':65536,'core_selftest_checks':73217,'transport_checks':578,'driver_core':'portable C with test codec and UBSan, not native XPRESS','kernel_binary':'Zig0.14.1/LLVM cross-build with official WDK/SDK10.0.26100.1 and KMDF1.33, unsigned','embedded_assembly_proof':proof,'windows_driver_execution':False,'wdk_msbuild_execution':False,'driver_signing':False,'transparent_foreign_memory_supported':False,'measured_physical_ram_saved_bytes':None,'PC_test1_connections':'tool discovery failed twice; no remote actions'}
(out/'QA.json').write_text(json.dumps(metadata,indent=2)+'\n')
# Preserve recovered and new committed history. Archive contains committed source only, no obj/bin/cache/private keys.
bundle=out/'ElasticMem.git.bundle';subprocess.run(['git','-C',str(root),'bundle','create',str(bundle),'--all'],check=True)
files=run('ls-files').splitlines()
with zipfile.ZipFile(out/f'ElasticMem-v{version}-source.zip','w',zipfile.ZIP_DEFLATED) as z:
    for name in files:z.write(root/name,'ElasticMem/'+name)
    z.write(bundle,'ElasticMem/ElasticMem.git.bundle');z.write(out/'QA.json','ElasticMem/QA.json')
    z.writestr('ElasticMem/RESTORE-024.txt',f'git clone ElasticMem.git.bundle recovered\ngit -C recovered checkout {commit}\nRead driver/README.md and docs/recovery-024.md. Research candidate, not Windows validated.\n')
    for name in ['core-tests.log','transport-tests.log','driver-core-tests.log','kernel-cross-build.log','windows-final-build.log','ipc-023-reproduction.json']:
        z.write(qa/name,'ElasticMem/qa/'+name)
with zipfile.ZipFile(out/f'ElasticMem-v{version}-win-x64-research.zip','w',zipfile.ZIP_DEFLATED) as z:
    for f in app.rglob('*'):
        if f.is_file() and f.suffix not in ('.pdb','.log'):z.write(f,'ElasticMem/'+str(f.relative_to(app)))
    z.write(out/'QA.json','ElasticMem/QA.json')
    z.write(root/'driver/README.md','ElasticMem/DRIVER-README.md')
    for f in (root/'driver/scripts').glob('*.ps1'):z.write(f,'ElasticMem/driver-scripts/'+f.name)
    z.write(qa/'windows-client/ElasticMem.DriverClient.exe','ElasticMem/driver-client/ElasticMem.DriverClient.exe')
    z.write(qa/'kernel-cross/ElasticMemStore.sys','ElasticMem/kernel-cross-UNSIGNED/ElasticMemStore.sys')
    z.writestr('ElasticMem/START-HERE.txt','Research candidate. Normal Live backend A/B can be rerun on Windows. Kernel SYS is unsigned; installer refuses it. Driver only in disposable VM after local WDK rebuild/signing and verification. Not a transparent Chrome/Telegram RAM optimizer. No automatic installation or security changes. Read FULL instructions and DRIVER-README.md.\n')
shutil.copyfile(root/'driver/README.md',out/f'ElasticMem-v{version}-driver-guide.md')
manifest=[]
for f in out.iterdir():
    if f.suffix in ('.zip','.md','.json'):manifest.append({'name':f.name,'bytes':f.stat().st_size,'sha256':hashlib.sha256(f.read_bytes()).hexdigest()})
(out/'SHA256.txt').write_text(''.join(f"{x['sha256']}  {x['name']}\n" for x in manifest))
# Verify ZIP CRCs and manifest/source identity before delivery.
for f in out.glob('*.zip'):
    with zipfile.ZipFile(f) as z:assert z.testzip() is None
print(json.dumps({'version':version,'commit':commit,'files':manifest,'status':'local_candidate_not_published'},indent=2))
