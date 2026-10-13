"""Fail-closed inventory: no recursive approval of arbitrary output bytes."""
import hashlib,pathlib,json
PUBLISHED={'ElasticMem.exe','ElasticMem.pdb','ElasticMem.Core.pdb'}
SUPPLEMENTS={'THIRD-PARTY-NOTICES.txt','CHECK-ALL.cmd','CHECK-VRAM.cmd','CHECK-WINDOWS.cmd','README-RESIDENT-G1.md','README-WINDOWS.md','ПОЛНАЯ-ИНСТРУКЦИЯ.md','examples/metadata.json','examples/trace.csv','publish.log'}
BASE=PUBLISHED|SUPPLEMENTS
FINAL=BASE|{'BUILD.json','QA.json','core-tests-linux.log'}
def validate_paths(root,expected):
 root=pathlib.Path(root)
 if root.is_symlink():raise ValueError('Symlink staging root')
 files=set()
 for f in root.rglob('*'):
  if f.is_symlink():raise ValueError('Symlink artifact')
  if f.is_file():files.add(f.relative_to(root).as_posix())
  elif not f.is_dir():raise ValueError('Special artifact')
 if files!=set(expected):raise ValueError('Unexpected/missing artifacts: '+repr(files^set(expected)))
 return files
def inventory(root,expected,extra=()):
 validate_paths(root,set(expected)|set(extra))
 return [{'path':n,'bytes':(root/n).stat().st_size,'sha256':hashlib.sha256((root/n).read_bytes()).hexdigest()} for n in sorted(expected)]
def verify_build(root,build):
 validate_paths(root,BASE|{'BUILD.json'})
 items=build['files']
 if len(items)!=len(BASE) or {i['path'] for i in items}!=BASE:raise ValueError('Not approved staging inventory')
 if items!=inventory(root,BASE,{'BUILD.json'}):raise ValueError('Staging inventory hash mismatch')
