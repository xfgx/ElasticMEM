#!/usr/bin/env python3
"""Upload only checked release files. Token stays in an external chmod-600 curl config, never in source/app/argv."""
import argparse,hashlib,json,pathlib,subprocess,re
p=argparse.ArgumentParser();p.add_argument('--request',required=True);p.add_argument('--directory',required=True);p.add_argument('--curl-config',required=True);a=p.parse_args();request=json.loads(pathlib.Path(a.request).read_text());folder=pathlib.Path(a.directory);config=pathlib.Path(a.curl_config)
if config.stat().st_mode&0o077:raise SystemExit('Private curl config must not be group/world accessible')
version=request['version']
if re.fullmatch(r'\d+\.\d+\.\d+',version) is None:raise SystemExit('Version format')
expected={f'ElasticMem-v{version}-win-x64.zip',f'ElasticMem-v{version}-source.zip',f'ElasticMem-v{version}-guide.md',f'ElasticMem-v{version}-SHA256.txt'}
if len(request['files'])!=4 or {f['name'] for f in request['files']}!=expected:raise SystemExit('Only four named build artifacts may be uploaded')
for f in request['files']:
 path=folder/f['name']
 if path.is_symlink() or path.resolve().parent!=folder.resolve():raise SystemExit('Unsafe publication path')
 data=path.read_bytes()
 if len(data)!=f['bytes'] or hashlib.sha256(data).hexdigest()!=f['sha256']:raise SystemExit('Local release hash mismatch')
 subprocess.run(['curl','--config',str(config),'--silent','--show-error','--fail-with-body','--max-time','300','--upload-file',str(path),'https://citpmcpmy.eastus.cloudapp.azure.com/elasticmem/publish/'+f['name']],check=True,stdout=subprocess.DEVNULL)
print(json.dumps({'uploaded_files':len(request['files']),'version':request['version'],'next':'Run publish-test1-server.py through test1 MCP, then independently verify public manifest and download SHA256.'}))
