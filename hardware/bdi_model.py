"""Эталонная (golden) модель сжатия кеш-линий 64 Б по схеме база + смещения (BΔI) с неявной нулевой базой.
Именно её реализует аппаратный блок (bdi_rtl.py); симуляция RTL сверяется с этой моделью бит в бит."""
LINE = 64

# (id, имя, размер элемента k байт, размер смещения d байт); размер = k + маска + n*d
CONFIGS = [
    (2, "B8D1", 8, 1), (5, "B4D1", 4, 1), (3, "B8D2", 8, 2), (7, "B2D1", 2, 1),
    (6, "B4D2", 4, 2), (4, "B8D4", 8, 4),
]
ENC_ZERO, ENC_REP, ENC_RAW = 0, 1, 15
NAMES = {0: "Zeros", 1: "Repeat", 15: "Raw", **{c[0]: c[1] for c in CONFIGS}}


def cfg_size(k, d):
    n = LINE // k
    return k + (n + 7) // 8 + n * d


SIZES = {ENC_ZERO: 0, ENC_REP: 8, ENC_RAW: 64, **{c[0]: cfg_size(c[2], c[3]) for c in CONFIGS}}
ORDER = sorted(CONFIGS, key=lambda c: cfg_size(c[2], c[3]))


def fits_signed(x, kbits, dbits):
    """x — kbits-битное число; помещается ли оно как знаковое в dbits бит."""
    top = x >> (dbits - 1)
    return top == 0 or top == (1 << (kbits - dbits + 1)) - 1


def elems(line: bytes, k):
    return [int.from_bytes(line[i:i + k], "little") for i in range(0, LINE, k)]


def compress(line: bytes):
    """→ (enc, payload_int, size_bytes). payload — little-endian битовая упаковка."""
    v = int.from_bytes(line, "little")
    if v == 0:
        return ENC_ZERO, 0, 0
    w = elems(line, 8)
    if all(x == w[0] for x in w):
        return ENC_REP, w[0], 8
    for enc, _, k, d in ORDER:
        kb, db, n = 8 * k, 8 * d, LINE // k
        e = elems(line, k)
        base = e[0]
        mask, deltas, ok = 0, [], True
        for i, x in enumerate(e):
            dz = x  # от неявной нулевой базы
            db_ = (x - base) & ((1 << kb) - 1)
            if fits_signed(dz, kb, db):
                deltas.append(dz & ((1 << db) - 1))
            elif fits_signed(db_, kb, db):
                mask |= 1 << i
                deltas.append(db_ & ((1 << db) - 1))
            else:
                ok = False
                break
        if not ok:
            continue
        mbytes = (n + 7) // 8
        p = base | (mask << kb)
        off = kb + 8 * mbytes
        for i, dl in enumerate(deltas):
            p |= dl << (off + i * db)
        return enc, p, cfg_size(k, d)
    return ENC_RAW, v, 64


def sext(x, fr, to):
    if x >> (fr - 1) & 1:
        x |= ((1 << to) - 1) ^ ((1 << fr) - 1)
    return x


def extract_word(enc, p, idx):
    """Выборочное чтение: 64-битное слово idx (0..7) прямо из сжатой линии, без восстановления всей линии."""
    if enc == ENC_ZERO:
        return 0
    if enc == ENC_REP:
        return p & (2**64 - 1)
    if enc == ENC_RAW:
        return (p >> (64 * idx)) & (2**64 - 1)
    _, _, k, d = next(c for c in CONFIGS if c[0] == enc)
    kb, db, n = 8 * k, 8 * d, LINE // k
    base = p & ((1 << kb) - 1)
    mask = (p >> kb) & ((1 << n) - 1)
    off = kb + 8 * ((n + 7) // 8)
    per = 8 // k
    out = 0
    for t in range(per):
        i = idx * per + t
        dl = sext((p >> (off + i * db)) & ((1 << db) - 1), db, kb)
        val = ((base if mask >> i & 1 else 0) + dl) & ((1 << kb) - 1)
        out |= val << (kb * t)
    return out


def decompress(enc, p):
    return b"".join(extract_word(enc, p, i).to_bytes(8, "little") for i in range(8))
