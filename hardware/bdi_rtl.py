"""Аппаратный блок BΔI на PyRTL: компрессор линии 64 Б, полный декомпрессор и блок выборочного чтения слова.
Генерирует синтезируемый Verilog; симуляция сверяется с эталонной моделью bdi_model.py."""
import sys, random
import pyrtl
from pyrtl.corecircuits import and_all_bits, or_all_bits
from bdi_model import CONFIGS, ORDER, ENC_ZERO, ENC_REP, ENC_RAW, cfg_size, compress, extract_word, LINE

LB = LINE * 8  # 512 бит


def fits(x, kb, db):
    top = x[db - 1:kb]
    return and_all_bits(top) | ~or_all_bits(top)


def build_compressor(line):
    """→ enc (4 бит), size (7 бит), payload (512 бит). Все варианты проверяются параллельно за один такт."""
    zero = ~or_all_bits(line)
    w = [line[i * 64:(i + 1) * 64] for i in range(8)]
    rep = and_all_bits(pyrtl.concat_list([w[i] == w[0] for i in range(1, 8)]))
    cand = []
    for enc, _, k, d in ORDER:
        kb, db, n = 8 * k, 8 * d, LINE // k
        e = [line[i * kb:(i + 1) * kb] for i in range(n)]
        base = e[0]
        okbits, maskbits, deltas = [], [], []
        for x in e:
            dz = fits(x, kb, db)
            diff = (x - base)[:kb]
            dbs = fits(diff, kb, db)
            okbits.append(dz | dbs)
            maskbits.append(~dz)                      # 1 = от явной базы
            deltas.append(pyrtl.select(dz, x[:db], diff[:db]))
        ok = and_all_bits(pyrtl.concat_list(okbits))
        mbytes = (n + 7) // 8
        mask = pyrtl.concat_list(maskbits).zero_extended(8 * mbytes)
        payload = pyrtl.concat_list([base, mask] + deltas).zero_extended(LB)
        cand.append((enc, ok, payload, cfg_size(k, d)))
    enc_o = pyrtl.WireVector(4, "enc_o_int"); size_o = pyrtl.WireVector(7, "size_o_int"); pay_o = pyrtl.WireVector(LB, "pay_o_int")
    # приоритетный выбор наименьшего подходящего формата
    e_val, s_val, p_val = pyrtl.Const(ENC_RAW, 4), pyrtl.Const(64, 7), line
    for enc, ok, payload, sz in reversed(cand):
        e_val = pyrtl.select(ok, pyrtl.Const(enc, 4), e_val)
        s_val = pyrtl.select(ok, pyrtl.Const(sz, 7), s_val)
        p_val = pyrtl.select(ok, payload, p_val)
    e_val = pyrtl.select(rep, pyrtl.Const(ENC_REP, 4), e_val)
    s_val = pyrtl.select(rep, pyrtl.Const(8, 7), s_val)
    p_val = pyrtl.select(rep, w[0].zero_extended(LB), p_val)
    e_val = pyrtl.select(zero, pyrtl.Const(ENC_ZERO, 4), e_val)
    s_val = pyrtl.select(zero, pyrtl.Const(0, 7), s_val)
    p_val = pyrtl.select(zero, pyrtl.Const(0, LB), p_val)
    enc_o <<= e_val; size_o <<= s_val; pay_o <<= p_val
    return enc_o, size_o, pay_o


def build_extract(enc, pay, idx):
    """Выборочное чтение: 64-битное слово idx прямо из сжатой линии (без восстановления всей линии)."""
    outs = {ENC_ZERO: pyrtl.Const(0, 64), ENC_REP: pay[:64]}
    raw_words = [pay[i * 64:(i + 1) * 64] for i in range(8)]
    outs[ENC_RAW] = pyrtl.mux(idx, *raw_words)
    for enc_id, _, k, d in CONFIGS:
        kb, db, n = 8 * k, 8 * d, LINE // k
        base = pay[:kb]
        mask = pay[kb:kb + n]
        off = kb + 8 * ((n + 7) // 8)
        per = 8 // k
        words = []
        for wi in range(8):  # для каждого возможного idx — свои элементы
            parts = []
            for t in range(per):
                i = wi * per + t
                dl = pay[off + i * db: off + (i + 1) * db].sign_extended(kb)
                parts.append((pyrtl.select(mask[i], base, pyrtl.Const(0, kb)) + dl)[:kb])
            words.append(pyrtl.concat_list(parts))
        outs[enc_id] = pyrtl.mux(idx, *words)
    res = pyrtl.Const(0, 64)
    for k_, v in outs.items():
        res = pyrtl.select(enc == k_, v, res)
    return res


def build(top="compressor"):
    pyrtl.reset_working_block()
    if top == "compressor":
        line = pyrtl.Input(LB, "line")
        e, s, p = build_compressor(line)
        o1 = pyrtl.Output(4, "enc"); o1 <<= e
        o2 = pyrtl.Output(7, "size"); o2 <<= s
        o3 = pyrtl.Output(LB, "payload"); o3 <<= p
    elif top == "extract":
        enc = pyrtl.Input(4, "enc"); pay = pyrtl.Input(LB, "payload"); idx = pyrtl.Input(3, "idx")
        o = pyrtl.Output(64, "word"); o <<= build_extract(enc, pay, idx)
    elif top == "decompressor":
        enc = pyrtl.Input(4, "enc"); pay = pyrtl.Input(LB, "payload")
        words = [build_extract(enc, pay, pyrtl.Const(i, 3)) for i in range(8)]
        o = pyrtl.Output(LB, "line"); o <<= pyrtl.concat_list(words)
    elif top == "regwrap":
        # для оценки частоты: регистры на входе и выходе вокруг компрессора и выборочного чтения
        line_i = pyrtl.Input(64, "din"); sel = pyrtl.Input(3, "din_sel"); idx_i = pyrtl.Input(3, "idx")
        regs = [pyrtl.Register(64, f"lr{i}") for i in range(8)]
        for i, r in enumerate(regs):
            r.next <<= pyrtl.select(sel == i, line_i, r)
        e, s, p = build_compressor(pyrtl.concat_list(regs))
        er, pr = pyrtl.Register(4, "er"), pyrtl.Register(LB, "pr")
        er.next <<= e; pr.next <<= p
        ir = pyrtl.Register(3, "ir"); ir.next <<= idx_i
        wr = pyrtl.Register(64, "wr"); wr.next <<= build_extract(er, pr, ir)
        sr = pyrtl.Register(7, "sr"); sr.next <<= s
        o1 = pyrtl.Output(64, "word"); o1 <<= wr
        o2 = pyrtl.Output(4, "enc"); o2 <<= er
        o3 = pyrtl.Output(7, "size"); o3 <<= sr


def gen_lines(n, real=None):
    rnd = random.Random(7)
    out = []
    kinds = ["zero", "rep", "small", "ptr", "b4", "b2", "rand", "mix"]
    for i in range(n):
        if real is not None and i % 2 == 0:
            j = rnd.randrange(len(real) // 64); out.append(real[j * 64:(j + 1) * 64]); continue
        kd = kinds[i % len(kinds)]
        if kd == "zero": b = bytes(64)
        elif kd == "rep": b = rnd.getrandbits(64).to_bytes(8, "little") * 8
        elif kd == "small": b = b"".join(rnd.randrange(-100, 100).to_bytes(8, "little", signed=True) for _ in range(8))
        elif kd == "ptr":
            base = 0x7F00_0000_0000 + rnd.getrandbits(32)
            b = b"".join(((base + rnd.randrange(-30000, 30000)) if rnd.random() < .7 else rnd.randrange(50)).to_bytes(8, "little") for _ in range(8))
        elif kd == "b4":
            base = rnd.getrandbits(32); b = b"".join(((base + rnd.randrange(-120, 120)) & 0xFFFFFFFF).to_bytes(4, "little") for _ in range(16))
        elif kd == "b2":
            base = rnd.getrandbits(16); b = b"".join(((base + rnd.randrange(-100, 100)) & 0xFFFF).to_bytes(2, "little") for _ in range(32))
        elif kd == "rand": b = rnd.randbytes(64)
        else: b = bytes(rnd.choice([0, 0, 1, 255, rnd.randrange(256)]) for _ in range(64))
        out.append(b)
    return out


def verify(n=3000, real_path=None):
    real = open(real_path, "rb").read() if real_path else None
    lines = gen_lines(n, real)
    build("compressor")
    sim = pyrtl.FastSimulation()
    bad = 0
    results = []
    for b in lines:
        sim.step({"line": int.from_bytes(b, "little")})
        enc, size, pay = sim.inspect("enc"), sim.inspect("size"), sim.inspect("payload")
        e2, p2, s2 = compress(b)
        if (enc, size, pay) != (e2, s2, p2): bad += 1
        results.append((enc, pay, b))
    build("extract")
    sim = pyrtl.FastSimulation()
    bad_x = 0
    rnd = random.Random(3)
    for enc, pay, b in results:
        idx = rnd.randrange(8)
        sim.step({"enc": enc, "payload": pay, "idx": idx})
        if sim.inspect("word") != int.from_bytes(b[idx * 8:idx * 8 + 8], "little"): bad_x += 1
    print(f"RTL компрессор: {n} линий, расхождений с эталоном: {bad}")
    print(f"RTL выборочное чтение: {n} проверок, ошибок: {bad_x}")
    return bad == 0 and bad_x == 0


def export():
    for t in ["compressor", "extract", "decompressor", "regwrap"]:
        build(t)
        with open(f"verilog/bdi_{t}.v", "w") as f:
            pyrtl.output_to_verilog(f, add_reset=(t == "regwrap"), block=None)
        print("Verilog:", f"verilog/bdi_{t}.v")


if __name__ == "__main__":
    import os
    os.makedirs("verilog", exist_ok=True)
    ok = verify(int(sys.argv[1]) if len(sys.argv) > 1 else 3000, sys.argv[2] if len(sys.argv) > 2 else None)
    if ok: export()
