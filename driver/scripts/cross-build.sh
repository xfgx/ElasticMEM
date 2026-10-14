#!/usr/bin/env bash
# Research COFF object cross-build only. Never links an installable SYS.
# Use Native-Relink.ps1 on Windows; legacy LLD images failed VM testing.
set -euo pipefail
: "${ZIG:?Path to Zig 0.14.1 executable required}"
: "${WDK_ROOT:?Extracted Microsoft.Windows.WDK.x64 c/ directory required}"
: "${SDK_ROOT:?Extracted Microsoft.Windows.SDK.CPP c/ directory required}"
root=$(cd "$(dirname "$0")/../.." && pwd)
out=${1:-"$root/driver/out/cross"}
mkdir -p "$out"
if [[ -e "$out/ElasticMemStore.sys" ]]; then
  echo "Refusing directory containing a stale SYS. Use a fresh output directory." >&2
  exit 1
fi
inc=( -isystem "$WDK_ROOT/Include/10.0.26100.0/km" -isystem "$WDK_ROOT/Include/10.0.26100.0/km/crt" -isystem "$WDK_ROOT/Include/10.0.26100.0/shared" -isystem "$SDK_ROOT/Include/10.0.26100.0/shared" -isystem "$WDK_ROOT/Include/wdf/kmdf/1.33" )
flags=( -target x86_64-windows-msvc -O2 -ffreestanding -mno-sse -mno-sse2 -mno-red-zone -fno-builtin -fno-vectorize -fno-slp-vectorize -fstack-protector-strong -fms-extensions -fms-compatibility -Xclang -cfguard -D_AMD64_ -D_WIN64 -D_KERNEL_MODE -D_WIN32_WINNT=0x0A00 -DNTDDI_VERSION=0x0A00000B -DKMDF_VERSION_MAJOR=1 -DKMDF_VERSION_MINOR=33 -Wall -Wextra -Werror -Wno-pragma-pack -Wno-ignored-pragma-intrinsic -Wno-nonportable-include-path )
"$ZIG" cc "${flags[@]}" "${inc[@]}" -c "$root/driver/kmdf/driver.c" -o "$out/driver.obj"
"$ZIG" cc "${flags[@]}" "${inc[@]}" -c "$root/driver/core/em_store.c" -o "$out/em_store.obj"
echo 'OBJECTS ONLY: native Microsoft link.exe or WDK/MSBuild required to produce SYS.'
echo 'Legacy LLD SYS failed Windows VM loading and must NOT be installed.'
