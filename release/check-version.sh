#!/usr/bin/env bash
# Защита релиза: собранная программа должна называть себя той же версией, что и релиз.
# Иначе updater (--version-file) откажется ставить «не более новую» версию — как случилось с 0.34.0.
set -euo pipefail
want="$1"; root="$(cd "$(dirname "$0")/.." && pwd)"
fail=0
for proj in src/ElasticMem.App/ElasticMem.App.csproj src/ElasticMem.Vmm/ElasticMem.Vmm.csproj tools/ElasticMem.VmmRunner/ElasticMem.VmmRunner.csproj; do
  got=$(grep -o '<Version>[^<]*' "$root/$proj" | sed 's/<Version>//')
  if [ "$got" != "$want" ]; then echo "FAIL $proj: Version=$got, ожидается $want"; fail=1; else echo "ok   $proj: $got"; fi
done
# Версия, вшитая в собранную сборку приложения (AssemblyInformationalVersion = "<версия>+<commit>").
dll="$root/src/ElasticMem.App/bin/Release/net8.0-windows/win-x64/ElasticMem.dll"
if [ -f "$dll" ]; then
  if grep -aq "$want+" "$dll"; then echo "ok   собранная ElasticMem.dll содержит $want"; else echo "FAIL собранная ElasticMem.dll не содержит версию $want"; fail=1; fi
else echo "FAIL нет собранной $dll"; fail=1; fi
[ $fail -eq 0 ] && echo "VERSION CHECK PASS $want" || { echo "VERSION CHECK FAILED"; exit 1; }
