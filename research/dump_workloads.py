"""Снимки памяти разных процессов (Linux /proc/self/mem) для поиска связей между живыми буферами."""
import re, sys, os, sqlite3, random
import numpy as np

def dump(path, limit=96 << 20):
    total = 0
    with open(path, "wb") as out, open("/proc/self/maps") as maps, open("/proc/self/mem", "rb", 0) as mem:
        for line in maps:
            m = re.match(r"([0-9a-f]+)-([0-9a-f]+) (\S+) \S+ \S+ (\d+)\s*(.*)", line)
            lo, hi, perm, inode, name = int(m[1], 16), int(m[2], 16), m[3], m[4], m[5]
            if not perm.startswith("rw") or inode != "0" or name.startswith("[v"):
                continue
            size = min(hi - lo, limit - total)
            try:
                mem.seek(lo); out.write(mem.read(size)); total += size
            except OSError:
                pass
            if total >= limit:
                break
    print(path, total >> 20, "МБ")

kind = sys.argv[1]
rng = np.random.default_rng(5)
if kind == "sqlite":
    # реальная СУБД: таблица + индексы (индекс — переупорядоченная копия столбца)
    db = sqlite3.connect(":memory:")
    db.execute("pragma cache_size=-200000")
    db.execute("create table t(id integer primary key, price integer, qty integer, city text, ts integer)")
    rows = [(i, random.randint(100, 100000), random.randint(0, 50), random.choice(["msk", "kzn", "prm"]), 1_700_000_000 + i) for i in range(600_000)]
    db.executemany("insert into t values (?,?,?,?,?)", rows)
    db.execute("create index ip on t(price)"); db.execute("create index iq on t(qty, ts)")
    db.execute("select sum(price) from t").fetchall()
    dump(sys.argv[2])
elif kind == "control":
    # положительный контроль: заведомо связанные живые буферы (AoS + SoA + int32→int64 + перестановка)
    n = 1_500_000
    aos = np.zeros(n, dtype=[("id", "<u8"), ("price", "<u8"), ("ts", "<u8"), ("w", "<u8")])
    aos["id"] = np.arange(n) * 7919 + 10**9; aos["price"] = rng.integers(10**6, 10**9, n)
    aos["ts"] = 1_700_000_000_000 + rng.integers(0, 10**9, n); aos["w"] = rng.integers(10**5, 10**12, n)
    soa_price = aos["price"].copy(); soa_ts = aos["ts"].copy()       # SoA-копии столбцов
    i32 = rng.integers(10**6, 2**31, n, dtype=np.uint32); i64 = i32.astype(np.uint64)   # расширение
    perm = rng.permutation(n); sorted_w = aos["w"][perm].copy()     # переупорядочивание записей
    dump(sys.argv[2])
