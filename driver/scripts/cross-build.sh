#!/usr/bin/env bash
# Research cross-build only. Does NOT certify loadability, HVCI or Windows runtime.
set -euo pipefail
: "${ZIG:?Path to Zig 0.14.1 executable required}"
: "${WDK_ROOT:?Extracted Microsoft.Windows.WDK.x64 c/ directory required}"
: "${SDK_ROOT:?Extracted Microsoft.Windows.SDK.CPP c/ directory required}"
root=$(cd "$(dirname "$0")/../.." && pwd)
out=${1:-"$root/driver/out/cross"}
mkdir -p "$out"
inc=( -isystem "$WDK_ROOT/Include/10.0.26100.0/km" -isystem "$WDK_ROOT/Include/10.0.26100.0/km/crt" -isystem "$WDK_ROOT/Include/10.0.26100.0/shared" -isystem "$SDK_ROOT/Include/10.0.26100.0/shared" -isystem "$WDK_ROOT/Include/wdf/kmdf/1.33" )
flags=( -target x86_64-windows-msvc -O2 -ffreestanding -mno-sse -mno-sse2 -mno-red-zone -fno-builtin -fno-vectorize -fno-slp-vectorize -fstack-protector-strong -fms-extensions -fms-compatibility -Xclang -cfguard -D_AMD64_ -D_WIN64 -D_KERNEL_MODE -D_WIN32_WINNT=0x0A00 -DNTDDI_VERSION=0x0A00000B -DKMDF_VERSION_MAJOR=1 -DKMDF_VERSION_MINOR=33 -Wall -Wextra -Werror -Wno-pragma-pack -Wno-ignored-pragma-intrinsic -Wno-nonportable-include-path )
"$ZIG" cc "${flags[@]}" "${inc[@]}" -c "$root/driver/kmdf/driver.c" -o "$out/driver.obj"
"$ZIG" cc "${flags[@]}" "${inc[@]}" -c "$root/driver/core/em_store.c" -o "$out/em_store.obj"
"$ZIG" lld-link /nologo /driver /subsystem:native,10.0 /entry:FxDriverEntry /nodefaultlib /machine:x64 /dynamicbase /highentropyva /nxcompat /integritycheck /guard:cf /release /brepro /debug /pdb:"$out/ElasticMemStore.pdb" /out:"$out/ElasticMemStore.sys" "$out/driver.obj" "$out/em_store.obj" "$WDK_ROOT/Lib/wdf/kmdf/x64/1.33/wdfdriverentry.lib" "$WDK_ROOT/Lib/wdf/kmdf/x64/1.33/wdfldr.lib" "$WDK_ROOT/Lib/10.0.26100.0/km/x64/ntoskrnl.lib" "$WDK_ROOT/Lib/10.0.26100.0/km/x64/bufferoverflowfastfailk.lib"
echo 'CROSS-BUILD ONLY: unsigned SYS; no WDK/MSBuild, Windows execution or load verification.'
