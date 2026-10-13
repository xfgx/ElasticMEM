"""Эксперимент 1 из гипотезы: есть ли в живой памяти связи между буферами, которые не видит постраничная дедупликация?
Ищем только простые точные отношения на уровне 8-байтовых слов:
  • точные дубликаты линий 64 Б (вне идентичных страниц 4 КБ);
  • расширение целых: 8 значений int32/uint32 ↔ 8 значений int64 в другом месте;
  • изменение шага (AoS↔SoA): 4 подряд идущих слова ↔ те же 4 слова с шагом s (2..16 слов) в другом месте.
Тривиальные блоки (все одинаковые, нули, малые счётчики 0..255) исключаются — они и так сжимаются.
Выход: доля памяти, которую можно было бы хранить как F(A) при НУЛЕВОМ остатке (оптимистическая граница)."""
import sys, numpy as np, hashlib
path = sys.argv[1]
raw = np.fromfile(path, dtype=np.uint64)
W = raw.size
print(f"Снимок {W*8>>20} МБ, {W} слов")

def mix(*cols):
    h = np.zeros(cols[0].shape, np.uint64)
    for c in cols:
        h = (h ^ c) * np.uint64(0x9E3779B97F4A7C15)
        h ^= h >> np.uint64(29)
    return h

nontriv_word = raw > 255
# --- 1. страницы 4 КБ: идентичные (это ОС уже умеет) ---
pages = raw[: W // 512 * 512].reshape(-1, 512)
ph = mix(*[pages[:, i] for i in range(512)])  # полный отпечаток страницы
uniq_p, cnt_p = np.unique(ph, return_counts=True)
dup_pages = int((cnt_p[cnt_p > 1] - 1).sum())
zero_pages = int((pages.max(axis=1) == 0).sum())
print(f"Идентичные страницы 4 КБ (их ОС уже может объединить): {100*dup_pages/len(pages):.1f}% (нулевых {100*zero_pages/len(pages):.1f}%)")

# --- 2. дубликаты линий 64 Б в неидентичных страницах ---
lines = raw[: W // 8 * 8].reshape(-1, 8)
lnt = (lines > 255).sum(axis=1) >= 4
lnt &= ~(lines == lines[:, :1]).all(axis=1)
lh = mix(*[lines[:, i] for i in range(8)])
# исключаем линии из страниц, которые и так идентичны другим (их уже может объединить ОС)
dup_mask_p = np.isin(ph, uniq_p[cnt_p > 1]) | (pages.max(axis=1) == 0)
in_dup_page = np.repeat(dup_mask_p, 64)
in_dup_page = np.concatenate([in_dup_page, np.zeros(len(lines) - in_dup_page.size, bool)])
lnt &= ~in_dup_page
lh_nt = lh[lnt]
u, inv, c = np.unique(lh_nt, return_inverse=True, return_counts=True)
dup_lines = int((c[c > 1] - 1).sum())
print(f"Нетривиальные линии 64 Б вне идентичных страниц: {100*lnt.mean():.1f}%; из них точные дубликаты где-то ещё: {100*dup_lines/max(1,lnt.sum()):.2f}%  (→ {100*dup_lines/len(lines):.2f}% памяти)")

# --- 3. расширение целых: блок из 8 uint32 ↔ 8 uint64 ---
u32 = raw.view(np.uint32)
blk32 = u32[: u32.size // 8 * 8].reshape(-1, 8).astype(np.uint64)
ok32 = ((blk32 > 255).sum(axis=1) >= 4) & ~(blk32 == blk32[:, :1]).all(axis=1)
h32 = mix(*[blk32[:, i] for i in range(8)])[ok32]
lines_nt_h = lh[lnt]
s32 = set(h32.tolist())
widened = sum(1 for x in lines_nt_h.tolist() if x in s32)
print(f"Линии int64, являющиеся расширением 8 int32 из другого места: {100*widened/len(lines):.3f}% памяти")
# sign-extended
sx = blk32.astype(np.int64).astype(np.uint64)
sx = np.where(blk32 >= 2**31, blk32 | np.uint64(0xFFFFFFFF00000000), blk32)
hs = set(mix(*[sx[:, i] for i in range(8)])[ok32].tolist())
widened_s = sum(1 for x in lines_nt_h.tolist() if x in hs)
print(f"  то же со знаковым расширением: {100*widened_s/len(lines):.3f}% памяти")

# --- 4. изменение шага: 4 подряд слова ↔ 4 слова с шагом s ---
G = 4
idx0 = np.arange(0, W - 64 * G, 4)          # кандидаты-«столбцы» (B), выровнено по 4 словам
contig = np.stack([raw[idx0 + k] for k in range(G)], axis=1)
okc = ((contig > 255).sum(axis=1) == G) & ~(contig == contig[:, :1]).all(axis=1) & (np.diff(contig.astype(np.int64), axis=1) != 0).all(axis=1)
hc = mix(*[contig[:, k] for k in range(G)])[okc]
setc = set(hc.tolist())
total_strided = 0
for s in range(2, 17):
    starts = np.arange(0, W - s * G)
    st = np.stack([raw[starts + k * s] for k in range(G)], axis=1)
    oks = ((st > 255).sum(axis=1) == G) & ~(st == st[:, :1]).all(axis=1)
    hs_ = mix(*[st[:, k] for k in range(G)])[oks]
    m = np.isin(hc, hs_)
    total_strided = max(total_strided, int(m.sum()))
    if m.sum():
        print(f"  шаг {s:2} слов: совпадений столбцов {int(m.sum())} ({100*m.sum()*G/W:.3f}% памяти)")
print(f"AoS↔SoA (лучший шаг): {100*total_strided*G/W:.3f}% памяти")
