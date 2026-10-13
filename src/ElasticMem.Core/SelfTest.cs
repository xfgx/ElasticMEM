using System.Text;
using ElasticMem.Core.Access;

namespace ElasticMem.Core;

/// <summary>Быстрая самопроверка всех механизмов на этом компьютере: кодеки, ElasticStore, ElasticArray64, LinkedView.</summary>
public static class SelfTest
{
    public static string Run(Action<string>? log = null)
    {
        var sb = new StringBuilder();
        int fails = 0;
        void Ok(string name, bool ok) { sb.AppendLine($"  {(ok ? "OK " : "ОШИБКА")}  {name}"); log?.Invoke(name + (ok ? " — ок" : " — ОШИБКА")); if (!ok) fails++; }
        var rnd = new Random(1);

        // 1. кодеки микроблоков
        bool codec = true;
        var opt = new CodecOptions { Headrooms = new[] { 0, 2, 4, 8 } };
        for (int rep = 0; rep < 300 && codec; rep++)
        {
            var block = new byte[1024];
            int kind = rep % 5;
            for (int i = 0; i < 128; i++)
                BitConverter.TryWriteBytes(block.AsSpan(i * 8), kind switch
                {
                    0 => 0UL, 1 => (ulong)rnd.Next(4096), 2 => 5_000_000UL + (ulong)rnd.Next(300),
                    3 => rnd.Next(10) == 0 ? (ulong)rnd.NextInt64() : 0, _ => (ulong)rnd.NextInt64(),
                });
            foreach (var c in BlockCodec.Candidates(block, opt))
            {
                var e = BlockCodec.Build(block, c, opt); var d = new byte[1024]; BlockCodec.Decode(e, d);
                if (!d.AsSpan().SequenceEqual(block)) { codec = false; break; }
            }
        }
        Ok("Кодеки микроблоков: обратимость всех форматов", codec);

        // 2. ElasticStore против эталона
        var st = new ElasticStore(256 * 1024, new ElasticStoreOptions { BlockSize = 512, MinDwellSec = 0 });
        var sh = new byte[st.LogicalSize]; var buf = new byte[600]; bool sok = true;
        for (int step = 0; step < 80_000 && sok; step++)
        {
            st.Now = step * 0.001; int len = rnd.Next(1, 600); long off = rnd.NextInt64(st.LogicalSize - len);
            if (rnd.Next(3) == 0) { for (int i = 0; i < len; i++) buf[i] = (byte)rnd.Next(4); st.Write(off, buf.AsSpan(0, len)); buf.AsSpan(0, len).CopyTo(sh.AsSpan((int)off)); }
            else { st.Read(off, buf.AsSpan(0, len)); sok = buf.AsSpan(0, len).SequenceEqual(sh.AsSpan((int)off, len)); }
            if (step % 50 == 0) st.Tick();
        }
        Ok($"ElasticStore (микроблоки + патчи): 80 000 операций без потерь, занято {st.Footprint / 1024} из {st.LogicalSize / 1024} КБ", sok);

        // 3. ElasticArray64
        long n = 200_000; var a = new ElasticArray64(n, new ElasticArrayOptions { BudgetBytes = n * 4, MinDwellEpochs = 1 });
        var sa = new ulong[n]; bool aok = true;
        for (int ep = 0; ep < 20 && aok; ep++)
        {
            for (int s = 0; s < 30_000; s++)
            {
                long i = rnd.NextInt64(n);
                if (rnd.Next(3) == 0) { ulong v = rnd.Next(2000) == 0 ? (ulong)rnd.NextInt64() : 1_000_000UL + (ulong)rnd.Next(5000); a.Set(i, v); sa[i] = v; }
                else if (a.Get(i) != sa[i]) { aok = false; break; }
            }
            a.Maintain();
        }
        for (long i = 0; i < n && aok; i++) aok = a.Get(i) == sa[i];
        Ok($"ElasticArray64 (сжатая основа + горячее окно): чтение/запись без потерь, занято {a.Footprint / 1024} из {n * 8 / 1024} КБ", aok);

        // 4. LinkedView
        int m = 1 << 16; var A = new ulong[m]; for (int i = 0; i < m; i++) A[i] = (ulong)rnd.Next(1 << 20);
        var tr = new AoSoATransform(m / 4, 4);
        var refA = (ulong[])A.Clone(); var refB = new ulong[m]; for (int i = 0; i < m; i++) refB[i] = refA[tr.Map(i)];
        var lv = new LinkedView(A, tr); bool lok = true;
        for (int s = 0; s < 200_000 && lok; s++)
        {
            int i = rnd.Next(m); ulong v = (ulong)rnd.Next(1 << 20); double u = rnd.NextDouble();
            if (u < 0.1) { lv.SetA(i, v); refA[i] = v; }
            else if (u < 0.2) { lv.SetB(i, v); refB[i] = v; }
            else if (u < 0.6) lok = lv.GetB(i) == refB[i];
            else lok = lv.GetA(i) == refA[i];
        }
        Ok($"LinkedView (B = F(A) ⊕ R): независимые записи в A и B, байт-в-байт; откат {(lv.Stats.Materialized ? "был" : "не понадобился")}", lok);

        sb.Insert(0, fails == 0 ? "Самопроверка пройдена.\n" : $"Самопроверка: {fails} ошибок.\n");
        return sb.ToString();
    }
}
