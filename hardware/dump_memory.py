"""Снимок реальной памяти процесса (Linux /proc/self/mem): Python + pandas с таблицей данных."""
import re, sys, random
import numpy as np, pandas as pd
random.seed(1)
df = pd.DataFrame({
    "id": np.arange(400_000), "price": np.random.randint(100, 100000, 400_000),
    "qty": np.random.randint(0, 50, 400_000), "ts": pd.date_range("2026-01-01", periods=400_000, freq="s"),
    "city": np.random.choice(["Москва", "Казань", "Пермь", "Томск"], 400_000),
})
objs = [{"user": f"u{i}", "score": i % 97, "tags": ["a", "b"][: i % 3]} for i in range(150_000)]
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 96 << 20
out = open(sys.argv[1], "wb")
total = 0
with open("/proc/self/maps") as maps, open("/proc/self/mem", "rb", 0) as mem:
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
print(f"снято {total >> 20} МБ")
