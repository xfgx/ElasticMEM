"""Сжимаемость реальной памяти аппаратным BΔI (64 Б) против Deflate по страницам 4 КБ; оценка трафика DRAM."""
import sys, random, zlib, collections
from bdi_model import compress, decompress, extract_word, NAMES
data = open(sys.argv[1], "rb").read()
random.seed(2)
nlines = len(data) // 64
sample = random.sample(range(nlines), min(int(sys.argv[2]) if len(sys.argv) > 2 else 150_000, nlines))
cnt, size_sum, sectors, ok = collections.Counter(), 0, 0, True
for li in sample:
    line = data[li * 64:(li + 1) * 64]
    enc, p, sz = compress(line)
    cnt[enc] += 1
    size_sum += sz
    sectors += 1 if sz <= 32 else 2  # DRAM-сектора по 32 Б (BC4 / секторные кеши)
    if li % 50 == 0:
        ok &= decompress(enc, p) == line and all(extract_word(enc, p, w) == int.from_bytes(line[w*8:w*8+8], "little") for w in range(8))
# LCP-подобная раскладка страницы для контроллера памяти: все линии страницы в слотах одного размера,
# невлезающие — в область исключений; + 64 Б метаданных на страницу. Адрес линии = база + i*слот (без поиска).
pg = random.sample(range(len(data) // 4096), 1500)
lcp = 0
for p in pg:
    sizes = [compress(data[p*4096 + i*64: p*4096 + (i+1)*64])[2] for i in range(64)]
    best = 4096
    for slot in (16, 24, 32, 40):
        exc = sum(1 for z in sizes if z > slot)
        best = min(best, 64 * slot + exc * 64 + 64)
    lcp += best
print(f"Ёмкость в LCP-раскладке (контроллер памяти): {100*lcp/(1500*4096):.1f}% от исходного → +{100*(1500*4096/lcp-1):.0f}% эффективной RAM")
pages = random.sample(range(len(data) // 4096), 2000)
defl = sum(min(4096, len(zlib.compress(data[p*4096:(p+1)*4096], 1))) for p in pages) / (2000 * 4096)
n = len(sample)
print(f"Линий проверено: {n}; без потерь: {'да' if ok else 'НЕТ'}")
for enc, c in cnt.most_common():
    print(f"  {NAMES[enc]:7} {100*c/n:5.1f}%")
print(f"BΔI 64 Б: средний размер {size_sum/n:.1f} Б → {100*size_sum/n/64:.1f}% (без метаданных 4 бит/линию)")
print(f"Deflate 4 КБ (уровень 1): {100*defl:.1f}%")
print(f"Трафик DRAM при секторах 32 Б: {100*sectors/(2*n):.1f}% от несжатого (линий в 1 сектор: {100*(2*n-sectors)/n:.1f}%)")
