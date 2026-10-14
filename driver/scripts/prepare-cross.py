#!/usr/bin/env python3
"""Download pinned official Microsoft header/lib packages for research cross-build, not installation."""
import argparse, pathlib, urllib.request, zipfile, hashlib, re, collections
p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();base=pathlib.Path(a.directory).resolve();base.mkdir(parents=True,exist_ok=True)
packs=[('microsoft.windows.wdk.x64','wdk','247b2919ae451f65ba5f1cd51c7c39730fb0fc383d607f3e8ab317fddc8a8239'),('microsoft.windows.sdk.cpp','sdk','b4730a467a8f29145fc0136b2b3f626767e985f6aa6e32f1352953c38d6ff5d2')]
for name,folder,expected in packs:
    version='10.0.26100.1';target=base/(name+'.nupkg');url=f'https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg'
    if not target.exists():urllib.request.urlretrieve(url,target)
    if hashlib.sha256(target.read_bytes()).hexdigest()!=expected:raise SystemExit('Package SHA256 mismatch: '+name)
    with zipfile.ZipFile(target) as z:
        for item in z.namelist():
            path=pathlib.PurePosixPath(item)
            if path.is_absolute() or '..' in path.parts:raise SystemExit('Unsafe archive path')
            if ('/Include/' in item or '/Lib/' in item) and not item.endswith('/'):z.extract(item,base/folder)
# Match Windows case-insensitive #include spellings without editing vendor file contents.
files=[q for q in base.rglob('*') if q.is_file() and q.suffix.lower() in ('.h','.inc') and not q.is_symlink()];cases=collections.defaultdict(set)
for q in files:
    for item in re.findall(r'#\s*include\s*[<"]([^">]+)',q.read_text(errors='ignore')):
        name=item.split('/')[-1].split('\\')[-1];cases[name.lower()].add(name)
for q in files:
    for name in cases[q.name.lower()]:
        alias=q.with_name(name)
        if not alias.exists():alias.symlink_to(q.name)
print('Set WDK_ROOT='+str(base/'wdk/c'));print('Set SDK_ROOT='+str(base/'sdk/c'));print('Requires Zig0.14.1; packages ~266MB total, extracted headers/libs additionally. No toolchain is included in release ZIP.')
