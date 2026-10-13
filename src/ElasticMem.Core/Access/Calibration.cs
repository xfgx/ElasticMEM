using System.Diagnostics;

namespace ElasticMem.Core.Access;

public static class Calibration
{
    private static CostModel? _measured;

    /// <summary>Измерить стоимости операций на этой машине (≈ несколько секунд, 64 МБ данных).</summary>
    public static CostModel Measure(int words = 8 << 20, Action<string>? log = null)
    {
        if (_measured != null) return _measured;
        var k = new CostModel();
        var rnd = new Random(5);
        int nr = 2_000_000;
        var idx = new long[nr];
        for (int i = 0; i < nr; i++) idx[i] = rnd.NextInt64(words);
        int chunks = words / ElasticArray64.ChunkWords;
        foreach (var r in Enum.GetValues<Rep>())
        {
            var a = new ElasticArray64(words, new ElasticArrayOptions { Costs = CostModel.Default });
            for (long i = 0; i < words; i++) a.Set(i, 1_000_000 + (ulong)rnd.Next(r == Rep.Sparse ? 0 : 4096) * (ulong)(r == Rep.Sparse && rnd.Next(10) != 0 ? 0 : 1));
            var sw = Stopwatch.StartNew();
            a.ForceAll(r);
            double enc = sw.Elapsed.TotalNanoseconds / chunks;
            if (r == Rep.Packed) k.EncodeChunk = enc;
            if (r == Rep.Dense) k.DenseEncodeChunk = enc;
            ulong sink = 0;
            sw.Restart();
            for (int i = 0; i < nr; i++) sink += a.Get(idx[i]);
            k.RandRead[(int)r] = sw.Elapsed.TotalNanoseconds / nr;
            sw.Restart();
            for (int rep = 0; rep < 3; rep++) sink += a.Sum(0, words);
            k.SeqElem[(int)r] = sw.Elapsed.TotalNanoseconds / (3.0 * words);
            if (r == Rep.Dense) k.DenseDecodeChunk = k.SeqElem[(int)r] * ElasticArray64.ChunkWords;
            if (r == Rep.Packed)
            {
                var buf = new ulong[ElasticArray64.ChunkWords];
                sw.Restart();
                for (long i = 0; i < words; i += buf.Length) a.ReadRange(i, buf);
                k.MaterializeChunk = sw.Elapsed.TotalNanoseconds / chunks;
            }
            sw.Restart();
            int nw = nr / 2;
            for (int i = 0; i < nw; i++) a.Set(idx[i], r == Rep.Sparse ? (a.Get(idx[i]) == 0 ? 0 : 1_000_001UL) : 1_000_000 + (ulong)(i & 1023));
            k.WriteFit[(int)r] = sw.Elapsed.TotalNanoseconds / nw - k.RandRead[(int)r] * (r == Rep.Sparse ? 1 : 0);
            if (r == Rep.Packed)
            {
                // запись невлезающих значений → слой изменений (≤ 16 на чанк, без переполнения)
                sw.Restart();
                int cnt = 0;
                for (int ch = 0; ch < chunks; ch++)
                    for (int t = 0; t < 16; t++) { a.Set((long)ch * ElasticArray64.ChunkWords + t * 31, ulong.MaxValue - (ulong)t); cnt++; }
                k.OverlayWrite = sw.Elapsed.TotalNanoseconds / cnt;
            }
            if (sink == 42) Console.Write("");
            log?.Invoke($"калибровка {r}: случайное {k.RandRead[(int)r]:F1} нс, послед. {k.SeqElem[(int)r]:F2} нс/эл");
        }
        return _measured = k;
    }
}

