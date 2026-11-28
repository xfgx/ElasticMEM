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
  ../configure --target-list=x86_64-softmmu --enable-whpx --enable-slirp --enable-sdl --disable-gtk \
    --disable-docs --disable-werror --with-pkgversion="ElasticVM-$rev" ${CROSS:+--cross-prefix=$CROSS}
fi
ninja -j"$jobs" qemu-system-x86_64.exe qemu-img.exe

rm -rf "$out"; mkdir -p "$out/share"
cp qemu-system-x86_64.exe qemu-img.exe "$out/"
"${CROSS:-}strip" "$out"/*.exe
# DLL окружения сборки, нужные исполняемым файлам (ldd в MSYS2 показывает их транзитивно)
if [ -n "${MSYSTEM_PREFIX:-}" ]; then
  for exe in "$out"/*.exe; do ldd "$exe" | awk '{print $3}'; done | grep -i "^${MSYSTEM_PREFIX}/bin/" | sort -u |
    while read -r dll; do cp "$dll" "$out/"; done
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
