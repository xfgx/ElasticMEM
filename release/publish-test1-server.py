#!/usr/bin/env python3
"""Run on test1 via its authenticated MCP shell; no embedded publishing credentials."""
import argparse,datetime,fcntl,hashlib,json,os,pathlib,re,shutil,subprocess,sys,tempfile,zipfile

def sha(path):
 with open(path,'rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def require(ok,message):
 if not ok:raise ValueError(message)
def version_tuple(s):
 require(re.fullmatch(r'\d+\.\d+\.\d+',s) is not None,'Version format');return tuple(map(int,s.split('.')))
def validate_zip(path,version,commit):
 with zipfile.ZipFile(path) as z:
  require(z.testzip() is None,'ZIP CRC mismatch');names=z.namelist();require(len(names)==len(set(names)),'Duplicate ZIP entries')
  require(all(not n.startswith('/') and '\\' not in n and '..' not in pathlib.PurePosixPath(n).parts for n in names),'Unsafe ZIP paths')
  build=json.loads(z.read('BUILD.json'));require(build['version']==version and build['code_commit']==commit,'BUILD provenance mismatch');require(build['source_dirty'] is False,'Dirty build')
  items=build['files'];require(set(names)=={'BUILD.json'}|{f['path'] for f in items},'Unlisted/missing ZIP artifact')
  for f in items:
   b=z.read(f['path']);require(len(b)==f['bytes'] and hashlib.sha256(b).hexdigest()==f['sha256'],'Artifact hash mismatch: '+f['path'])
  qa=json.loads(z.read('QA.json'));require(qa['code_commit']==commit and qa['core_suite']=='passed','QA provenance/status')
  require('CHECK-ALL.cmd' in names and 'ПОЛНАЯ-ИНСТРУКЦИЯ.md' in names,'One-click/instructions missing')
  exe=z.read('ElasticMem.exe');import struct
  off=struct.unpack_from('<I',exe,0x3c)[0];require(exe[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',exe,off+4)[0]==0x8664,'Not PE AMD64')
  return len(items)
def publish(request,base):
 root=pathlib.Path(base);incoming=root/'incoming';updates=root/'updates';backups=root/'backups';backups.mkdir(parents=True,exist_ok=True);version=request['version'];commit=request['code_commit'];version_tuple(version)
 require(re.fullmatch(r'[0-9a-f]{40}',commit) is not None,'Commit format');items=request['files'];expected={f'ElasticMem-v{version}-win-x64.zip',f'ElasticMem-v{version}-source.zip',f'ElasticMem-v{version}-guide.md',f'ElasticMem-v{version}-SHA256.txt'}
 require(len(items)==4 and {f['name'] for f in items}==expected,'Unexpected publication files');require(shutil.disk_usage(root).free>500*1024*1024,'Low server disk reserve')
 with open(root/'.publish.lock','a+') as lock:
  fcntl.flock(lock,fcntl.LOCK_EX);latest=updates/'latest.json';previous=latest.read_bytes();require(hashlib.sha256(previous).hexdigest()==request['expected_latest_sha256'],'Concurrent manifest change; stop, do not overwrite');require(version_tuple(version)>version_tuple(json.loads(previous)['version']),'No automatic downgrade/same-version overwrite')
  sources={}
  for item in items:
   require(re.fullmatch(r'[0-9a-f]{64}',item['sha256']) is not None,'Hash format');path=incoming/item['name'];require(path.is_file() and not path.is_symlink(),'Incoming file missing or symlink');require(path.stat().st_size==item['bytes'] and sha(path)==item['sha256'],'Incoming integrity mismatch');require(not (updates/item['name']).exists(),'Target already exists; no clobber');sources[item['name']]=path
  binary=f'ElasticMem-v{version}-win-x64.zip';count=validate_zip(sources[binary],version,commit)
  with zipfile.ZipFile(sources[f'ElasticMem-v{version}-source.zip']) as z:
   require(sum(i.file_size for i in z.infolist())<128*1024*1024,'Source size cap');require(z.testzip() is None and 'ElasticMem/ElasticMem.git.bundle' in z.namelist(),'Source archive/bundle invalid');require(json.loads(z.read('ElasticMem/QA.json'))['code_commit']==commit,'Source QA commit mismatch')
   with tempfile.TemporaryDirectory() as tmp:
    bundle=pathlib.Path(tmp)/'source.bundle';bundle.write_bytes(z.read('ElasticMem/ElasticMem.git.bundle'));repo=pathlib.Path(tmp)/'repo.git';subprocess.run(['git','clone','--bare','--quiet',str(bundle),str(repo)],check=True)
    actual=subprocess.check_output(['git','-C',str(repo),'rev-parse',f'elasticmem-app-v{version}^{{}}'],text=True).strip();require(actual==commit,'Source release tag mismatch')
  with zipfile.ZipFile(sources[binary]) as z:require(z.read('ПОЛНАЯ-ИНСТРУКЦИЯ.md')==sources[f'ElasticMem-v{version}-guide.md'].read_bytes(),'Public guide differs from embedded package guide')
  checksum_text=sources[f'ElasticMem-v{version}-SHA256.txt'].read_text()
  for item in items:
   if not item['name'].endswith('-SHA256.txt'):require(item['sha256']+'  '+item['name'] in checksum_text,'Checksum index incomplete')
  prefix='https://citpmcpmy.eastus.cloudapp.azure.com/elasticmem/';info={f['name']:f for f in items};utc=datetime.datetime.now(datetime.timezone.utc).isoformat();old_version=json.loads(previous)['version'];backup=backups/('latest-before-'+version+'-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%f')+'.json');backup.write_bytes(previous);backup.chmod(0o600)
  for name,path in sources.items():os.replace(path,updates/name);(updates/name).chmod(0o644)
  manifest={'version':version,'url':prefix+binary,'sha256':info[binary]['sha256'],'codeCommit':commit,'notes':request['notes'],'instructionsUrl':prefix+f'ElasticMem-v{version}-guide.md','sourceUrl':prefix+f'ElasticMem-v{version}-source.zip','checksumsUrl':prefix+f'ElasticMem-v{version}-SHA256.txt','publishedUtc':utc,'previousVersion':old_version}
  fd,tmp=tempfile.mkstemp(prefix='.latest-',dir=updates)
  try:
   with os.fdopen(fd,'w') as f:json.dump(manifest,f,ensure_ascii=False,indent=2);f.write('\n');f.flush();os.fsync(f.fileno())
   os.chmod(tmp,0o644);os.replace(tmp,latest)
  finally:
   if os.path.exists(tmp):os.unlink(tmp)
  for name,item in info.items():(updates/(name+'.sha256')).write_text(item['sha256']+'  '+name+'\n')
  return {'published':version,'code_commit':commit,'verified_release_files':count,'previous_release_retained':old_version,'manifest_url':prefix+'latest.json','url':manifest['url'],'sha256':manifest['sha256']}
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('request');p.add_argument('--base',default='/srv/elasticmem');a=p.parse_args()
 try:print(json.dumps(publish(json.loads(pathlib.Path(a.request).read_text()),a.base),indent=2))
 except Exception as ex:print('Publication refused: '+str(ex),file=sys.stderr);sys.exit(1)
