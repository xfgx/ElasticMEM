#!/usr/bin/env python3
"""Export Git history + sealed runs outside /data repository; no self-hash cycle."""
import argparse, json, shutil, subprocess, tempfile, zipfile
from pathlib import Path
from run import ROOT, digest, git
from verify import verify

def package(runs, output, preserved_files=()):
    output=Path(output).resolve()
    if output.is_relative_to(ROOT): raise ValueError('Export outside repository, then download/store externally')
    if output.exists(): raise ValueError('Refusing to overwrite export')
    if git('status','--porcelain','--untracked-files=all'): raise ValueError('Dirty tree: commit before export')
    commit=git('rev-parse','HEAD'); output.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory() as d:
        stage=Path(d); entries=[]
        subprocess.run(['git','-C',str(ROOT),'bundle','create',str(stage/'history.bundle'),'--all'],check=True,capture_output=True)
        subprocess.run(['git','-C',str(ROOT),'archive','--format=zip','--output',str(stage/'source.zip'),commit],check=True)
        statuses=[]
        for folder in runs:
            folder=Path(folder).resolve();v=verify(folder,True)
            if folder.name in [x['id'] for x in statuses]: raise ValueError('Duplicate run basename')
            dest=stage/'runs'/folder.name;shutil.copytree(folder,dest);verify(dest)
            statuses.append({'id':folder.name,**v})
        if preserved_files:
            preserved=stage/'preserved-dumps';preserved.mkdir()
            for path in preserved_files:
                src=Path(path).resolve();dst=preserved/src.name
                if dst.exists():raise ValueError('Duplicate preserved dump basename')
                before=digest(src);shutil.copyfile(src,dst)
                if digest(dst)!=before or digest(src)!=before:raise ValueError('Preserved dump changed during archive')
            (preserved/'PROVENANCE.txt').write_text('Preserved earlier Linux snapshots. Collection provenance incomplete. NOT Windows traces; lost D/E cannot be reproduced from these alone. No new experiment performed on them.\n')
        (stage/'RESTORE.md').write_text('''# Restore and verify

1. Extract this archive into a new directory.
2. `git clone history.bundle ElasticMem-restored`
3. Copy each directory under `runs/` to `ElasticMem-restored/research/ceiling/runs/`.
4. `python ElasticMem-restored/research/ceiling/verify.py <copied-run> --check-git`
5. Rerun G0 with retained inputs and parameters from manifest; use a NEW run ID.

`source.zip` is a convenience source snapshot. `history.bundle` retains commits/tags.
Preserved dumps, when present, have incomplete historical provenance and are not Windows data.
Hashes prove retained-file integrity, not truth of old experimental conclusions.
Store this export on YOUR computer/remote storage. A second file inside /data is NOT a backup.
The archive SHA256 is in the external .sha256 sidecar; there is no recursive self-hash.
''')
        for p in sorted(stage.rglob('*')):
            if p.is_file(): entries.append({'path':p.relative_to(stage).as_posix(),'bytes':p.stat().st_size,'sha256':digest(p)})
        (stage/'PACKAGE.json').write_text(json.dumps({'schema':'elasticmem.package.v1','commit':commit,'runs':statuses,'files':entries},indent=2)+'\n')
        with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED,compresslevel=3) as z:
            for p in sorted(stage.rglob('*')):
                if p.is_file():z.write(p,p.relative_to(stage).as_posix())
    sidecar=output.with_suffix(output.suffix+'.sha256');sidecar.write_text(digest(output)+'  '+output.name+'\n')
    return {'archive':str(output),'sha256_file':str(sidecar),'bytes':output.stat().st_size,'commit':commit}
if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--runs',nargs='+',required=True);p.add_argument('--output',required=True);p.add_argument('--preserved-files',nargs='*',default=[])
    a=p.parse_args();print(json.dumps(package(a.runs,a.output,a.preserved_files)))
