#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
dotnet=${DOTNET:-dotnet}
cd "$root"
if [[ -n $(git status --porcelain --untracked-files=all) ]]; then echo 'Commit sources before publishing' >&2; exit 1; fi
commit=$(git rev-parse HEAD)
out=${1:-/data/ElasticMem-0.15.1-win-x64}
[[ ! -e "$out" && ! -L "$out" ]] || { echo "Output must be a NEW directory" >&2; exit 1; }
mkdir -p "$out"
"$dotnet" publish src/ElasticMem.App/ElasticMem.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:RestoreLockedMode=true -p:SourceRevisionId="$commit" -p:SourceDirty=false -o "$out" > "$out/publish.log" 2>&1
cp windows-check/* "$out/"
mkdir -p "$out/examples"
cp research/ceiling/examples/* "$out/examples/"
COMMIT="$commit" OUT="$out" python3 - <<'PY'
import hashlib,json,os,pathlib,sys
sys.path.insert(0,"release")
from release_inventory import BASE,inventory
p=pathlib.Path(os.environ['OUT']);items=inventory(p,BASE)
(p/'BUILD.json').write_text(json.dumps({'schema':'elasticmem.windows.release.v1','version':'0.15.1','code_commit':os.environ['COMMIT'],'source_dirty':False,'rid':'win-x64','self_contained':True,'single_file':True,'windows_execution':'not_tested_here','files':items},indent=2)+'\n')
PY
[[ $(git rev-parse HEAD) == "$commit" && -z $(git status --porcelain --untracked-files=all) ]] || { echo 'Source changed during publish' >&2; exit 1; }
echo "Published $out at $commit"
