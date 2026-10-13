#!/usr/bin/env python3
"""Independent full HTTPS download validation after MCP publication."""
import argparse,hashlib,json,pathlib,urllib.request,urllib.parse,zipfile,re
BASE='https://citpmcpmy.eastus.cloudapp.azure.com/elasticmem/'
def download(url,path):
 req=urllib.request.Request(url,headers={'Cache-Control':'no-cache'})
 with urllib.request.urlopen(req,timeout=120) as r,open(path,'wb') as f:
  if urllib.parse.urlparse(r.url).netloc!=urllib.parse.urlparse(BASE).netloc:raise ValueError('Unexpected redirect host')
  h=hashlib.sha256();size=0
  while b:=r.read(1024*1024):f.write(b);h.update(b);size+=len(b)
 return size,h.hexdigest()
def validate_manifest(m,request):
 assert m['version']==request['version'] and m['codeCommit']==request['code_commit'],'Public manifest differs'
 digest=m.get('sha256')
 assert isinstance(digest,str) and re.fullmatch(r'[0-9a-f]{64}',digest),'Missing/malformed manifest SHA256'
 binary=next(i for i in request['files'] if i['name'].endswith('-win-x64.zip'))
 assert digest==binary['sha256'],'Manifest binary SHA differs'
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--request',required=True);p.add_argument('--output',required=True);a=p.parse_args();request=json.loads(pathlib.Path(a.request).read_text());out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=True)
 download(BASE+'latest.json',out/'latest.json');m=json.loads((out/'latest.json').read_text());validate_manifest(m,request)
 links={'-win-x64.zip':'url','-source.zip':'sourceUrl','-guide.md':'instructionsUrl','-SHA256.txt':'checksumsUrl'}
 for item in request['files']:
  url=BASE+urllib.parse.quote(item['name'])
  key=next(k for suffix,k in links.items() if item['name'].endswith(suffix));assert m[key]==BASE+item['name'],'Manifest URL differs'
  size,digest=download(url,out/item['name']);assert not item['name'].endswith('-win-x64.zip') or digest==m['sha256'],'Public updater SHA differs';assert size==item['bytes'] and digest==item['sha256'],'PUBLIC full file hash mismatch: '+item['name']
  if item['name'].endswith('.zip'):
   with zipfile.ZipFile(out/item['name']) as z:assert z.testzip() is None
 result={'public_verification':'passed','version':m['version'],'commit':m['codeCommit'],'fully_downloaded_files':4,'bytes':sum(i['bytes'] for i in request['files'])};(out/'PUBLIC-VERIFIED.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
