#!/usr/bin/env bash
# ElasticVM: QEMU из исходников upstream (тег закреплён в UPSTREAM) + очередь патчей patches/*.patch.
# Запуск: MSYS2 UCRT64 (CI, .github/workflows/elasticvm.yml) или Linux с кросс-компилятором mingw-w64
# (CROSS=x86_64-w64-mingw32- и x86_64-w64-mingw32-pkg-config с библиотеками Windows).
# Результат: $OUT = qemu-system-x86_64.exe, qemu-img.exe, нужные им DLL и share/ (прошивки) — та же раскладка,
# что у папки qemu\ набора, поэтому набор просто кладёт её рядом как elasticvm\.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
. "$here/UPSTREAM"
src=${SRC:-$PWD/qemu-src}
out=${OUT:-$PWD/elasticvm-out}
jobs=${JOBS:-$(nproc)}

if [ ! -d "$src/.git" ]; then
  git clone --quiet --depth 1 --branch "$QEMU_TAG" "$QEMU_URL" "$src"
fi
got=$(git -C "$src" rev-parse HEAD)
if [ "$got" = "$QEMU_COMMIT" ]; then
  git -C "$src" -c user.name=ElasticVM -c user.email=noreply@anthropic.com am --quiet "$here"/patches/*.patch
elif ! git -C "$src" log --format=%H | grep -qx "$QEMU_COMMIT"; then
  echo "upstream: ожидался $QEMU_COMMIT ($QEMU_TAG), в $src $got" >&2; exit 1
fi

rev=$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo local)
mkdir -p "$src/build"
cd "$src/build"
if [ ! -f build.ninja ]; then
  ../configure --target-list=x86_64-softmmu --enable-whpx --enable-slirp --disable-sdl --disable-gtk \
    --disable-docs --disable-werror --with-pkgversion="ElasticVM-$rev" ${CROSS:+--cross-prefix=$CROSS}
fi
# Без SDL: сборка UCRT64 с SDL2 2.32 на Windows Server 2022 при любом запуске (даже --version) показывала окно
# «Error» и висела (прогон 46137051632). Окно гостя у ElasticVM — RDP (как Enhanced Session у Hyper-V), не SDL.
ninja -j"$jobs" qemu-system-x86_64.exe qemu-img.exe

rm -rf "$out"; mkdir -p "$out/share"
cp qemu-system-x86_64.exe qemu-img.exe "$out/"
"${CROSS:-}strip" "$out"/*.exe
# DLL окружения сборки, нужные исполняемым файлам: замыкание по таблицам импорта (objdump -p), пока не перестанут
# добавляться новые. Не найденная ни в $DLLDIR, ни в системе DLL — ошибка сборки (иначе Windows показывает окно
# «не найдена DLL», и QEMU висит без вывода, как в прогоне 46133239913).
dlldir=${DLLDIR:-${MSYSTEM_PREFIX:+$MSYSTEM_PREFIX/bin}}
objdump=${CROSS:-}objdump
if [ -n "$dlldir" ]; then
  while :; do
    added=0
    for pe in "$out"/*.exe "$out"/*.dll; do
      [ -f "$pe" ] || continue
      for dll in $("$objdump" -p "$pe" | sed -n 's/^\s*DLL Name: //p' | tr -d '\r'); do
        [ -f "$out/$dll" ] && continue
        src_dll=$(find "$dlldir" -maxdepth 1 -iname "$dll" | head -1)
        if [ -n "$src_dll" ]; then cp "$src_dll" "$out/"; added=1; fi
      done
    done
    [ $added = 0 ] && break
  done
  missing=$(for pe in "$out"/*.exe "$out"/*.dll; do "$objdump" -p "$pe" | sed -n 's/^\s*DLL Name: //p' | tr -d '\r'; done | sort -uf |
    while read -r dll; do
      [ -f "$out/$dll" ] && continue
      ls "$out" | grep -qix "$dll" && continue
      case "$(echo "$dll" | tr 'A-Z' 'a-z')" in
        api-ms-win-*|ext-ms-*|kernel32.dll|user32.dll|gdi32.dll|advapi32.dll|shell32.dll|ole32.dll|oleaut32.dll|ws2_32.dll|winmm.dll|\
        msvcrt.dll|ucrtbase.dll|ntdll.dll|imm32.dll|setupapi.dll|version.dll|winhvplatform.dll|winhvemulation.dll|iphlpapi.dll|\
        dnsapi.dll|shlwapi.dll|comdlg32.dll|winspool.drv|uuid.dll|crypt32.dll|bcrypt.dll|secur32.dll|dwmapi.dll|uxtheme.dll|\
        cfgmgr32.dll|hid.dll|dinput8.dll|dxgi.dll|d3d11.dll|d3d9.dll|opengl32.dll|dsound.dll|xinput1_4.dll|wtsapi32.dll|\
        pathcch.dll|synchronization.dll|userenv.dll|netapi32.dll|mswsock.dll|rpcrt4.dll|usp10.dll|comctl32.dll|powrprof.dll|\
        winusb.dll|mpr.dll|msimg32.dll|dbghelp.dll|psapi.dll|wldap32.dll|normaliz.dll) ;;
        *) echo "$dll" ;;
      esac
    done)
  if [ -n "$missing" ]; then echo "НЕ НАЙДЕНЫ DLL: $missing" >&2; exit 1; fi
  echo "DLL в комплекте: $(cd "$out" && ls *.dll | tr '\n' ' ')"
fi
pb=../pc-bios
for f in bios-256k.bin kvmvapic.bin linuxboot_dma.bin vgabios-stdvga.bin efi-e1000e.rom edk2-licenses.txt; do cp "$pb/$f" "$out/share/"; done
for f in edk2-x86_64-code.fd edk2-i386-vars.fd; do
  if [ -f "pc-bios/$f" ]; then cp "pc-bios/$f" "$out/share/"; else bzip2 -dc "$pb/$f.bz2" > "$out/share/$f"; fi
done
cp -r "$pb/keymaps" "$out/share/"
cp ../COPYING ../COPYING.LIB "$out/"
{
  echo "ElasticVM $rev: QEMU $QEMU_TAG ($QEMU_COMMIT, $QEMU_URL) + патчи:"
  for p in "$here"/patches/*.patch; do echo "  $(basename "$p") sha256=$(sha256sum "$p" | cut -d' ' -f1)"; done
  echo "Исходный код: QEMU по тегу выше и патчи из репозитория xfgx/ElasticMEM, vm/elasticvm (GPL-2.0, см. COPYING)."
} > "$out/SOURCE.txt"
echo "готово: $out"
