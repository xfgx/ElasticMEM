#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
out=${1:-/data/qa024}
mkdir -p "$out"
if [[ -n "${ZIG:-}" ]]; then
  "$ZIG" cc -std=c11 -O1 -g -Wall -Wextra -Werror -fsanitize=undefined core/em_store.c tests/store_test.c -o "$out/store-test"
else
  gcc -std=c11 -O1 -g -Wall -Wextra -Werror -pedantic -fsanitize=address,undefined core/em_store.c tests/store_test.c -o "$out/store-test"
fi
"$out/store-test"
