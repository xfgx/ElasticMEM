#!/usr/bin/env bash
# STAGING ONLY: executed by the agent-controlled MCP deployment workflow. Private credentials external; app/user logs never uploaded.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
dotnet=${DOTNET:-dotnet}
: "${CURL_CONFIG:?Use a chmod-600 external curl config obtained through the authorized test1 connection}"
[[ -z $(git -C "$root" status --porcelain --untracked-files=all) ]] || { echo 'Commit sources before shipping' >&2; exit 1; }
version=$(python3 -c 'import xml.etree.ElementTree as E,sys;print(E.parse(sys.argv[1]).find(".//Version").text)' "$root/src/ElasticMem.App/ElasticMem.App.csproj")
output=${OUTPUT:-/data/ElasticMem-$version-win-x64}
artifacts=${ARTIFACTS:-/data/ElasticMem-$version-publication}
[[ ! -e "$artifacts" && ! -L "$artifacts" ]] || { echo "Artifact directory must be NEW" >&2; exit 1; }; mkdir -p "$artifacts"
previous=$(python3 - <<'PY'
import urllib.request,hashlib
r=urllib.request.Request('https://citpmcpmy.eastus.cloudapp.azure.com/elasticmem/latest.json',headers={'Cache-Control':'no-cache'})
with urllib.request.urlopen(r,timeout=30) as f:print(hashlib.sha256(f.read()).hexdigest())
PY
)
DOTNET="$dotnet" "$root/release/build-release.sh" "$output"
# Restore/build test harness in an isolated tracked-source copy, never change source lockfiles.
temp=$(mktemp -d);trap 'rm -rf "$temp"' EXIT
python3 - "$root" "$temp" <<'PY'
import pathlib,subprocess,shutil,sys
root=pathlib.Path(sys.argv[1]);dest=pathlib.Path(sys.argv[2])
for n in subprocess.check_output(['git','-C',str(root),'ls-files','-z'],text=True).split('\0')[:-1]:
 p=dest/n;p.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(root/n,p)
PY
"$dotnet" build "$temp/tests/ElasticMem.Tests" -c Release > "$artifacts/test-harness-build.log" 2>&1
cp "$root/src/ElasticMem.Core/bin/Release/net8.0/ElasticMem.Core.dll" "$temp/tests/ElasticMem.Tests/bin/Release/net8.0/ElasticMem.Core.dll"
"$dotnet" "$temp/tests/ElasticMem.Tests/bin/Release/net8.0/ElasticMem.Tests.dll" > "$artifacts/core-tests.log" 2>&1
python3 "$root/release/test-public-verifier.py" > "$artifacts/public-verifier-tests.log" 2>&1
python3 "$root/release/test-inventory.py" > "$artifacts/inventory-tests.log" 2>&1
python3 "$root/release/test-publisher.py" > "$artifacts/publisher-tests.log" 2>&1
python3 "$root/release/package-release.py" --publish-dir "$output" --core-log "$artifacts/core-tests.log" --output "$artifacts" --previous-manifest-sha256 "$previous"
python3 "$root/release/upload-test1.py" --request "$artifacts/publication-request.json" --directory "$artifacts" --curl-config "$CURL_CONFIG"
echo "STAGED_NOT_PUBLISHED: agent must now execute test1 MCP publisher and verify-public.py before reporting success."
