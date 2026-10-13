#!/usr/bin/env python3
import argparse, json, subprocess
from pathlib import Path
from run import digest

def verify(folder, check_git=False):
    folder=Path(folder).resolve(); m=json.loads((folder/'manifest.json').read_text())
    listed=set()
    for item in m['artifacts']:
        p=(folder/item['path']).resolve()
        if not p.is_relative_to(folder) or not p.is_file(): raise ValueError('Unsafe/missing artifact: '+item['path'])
        if p.stat().st_size!=item['bytes'] or digest(p)!=item['sha256']: raise ValueError('Hash mismatch: '+item['path'])
        if item['path'] in listed: raise ValueError('Duplicate artifact path')
        listed.add(item['path'])
    found={x.relative_to(folder).as_posix() for x in folder.rglob('*') if x.is_file() and x != folder/'manifest.json'}
    if found!=listed: raise ValueError('Unexpected/missing artifact files')
    for item in m['inputs']:
        p=(folder/item['path']).resolve()
        if not p.is_relative_to(folder) or item['path'] not in listed: raise ValueError('Unsafe/unlisted input path')
        if digest(p)!=item['sha256']: raise ValueError('Input hash mismatch')
    if check_git:
        root=Path(__file__).resolve().parents[2]
        actual=subprocess.check_output(['git','-C',str(root),'rev-parse','refs/tags/'+m['git_tag']+'^{commit}'],text=True).strip()
        if actual!=m['commit']: raise ValueError('Tag/commit mismatch')
    return {'status':'verified_hashes_not_measurement_validity','run_status':m['status'],'files':len(listed),'commit':m['commit']}
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('folder');p.add_argument('--check-git',action='store_true');a=p.parse_args()
    print(json.dumps(verify(a.folder,a.check_git)))
