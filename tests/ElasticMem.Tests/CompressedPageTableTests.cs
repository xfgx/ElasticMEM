using ElasticMem.Core;
using ElasticMem.Core.Backend.Vmm;
static class CompressedPageTableTests
{
    // Портативный кодек для теста (RLE): сжимает повторяющиеся байты, отвергает случайные.
    sealed class Rle : IPageCodec
    {
        public string Name => "test_rle";
        public bool TryCompress(ReadOnlySpan<byte> page, Span<byte> dst, out int written)
        {
            written = 0; int i = 0;
            while (i < page.Length) { int run = 1; while (i + run < page.Length && run < 255 && page[i + run] == page[i]) run++; if (written + 2 > dst.Length || written + 2 >= 4096) { written = 0; return false; } dst[written++] = (byte)run; dst[written++] = page[i]; i += run; }
            return true;
        }
        public bool TryDecompress(ReadOnlySpan<byte> src, Span<byte> page, out int written)
        {
            written = 0; if (src.Length % 2 != 0) return false;
            for (int i = 0; i < src.Length; i += 2) { int run = src[i]; if (written + run > page.Length) return false; page.Slice(written, run).Fill(src[i + 1]); written += run; }
            return written == 4096;
        }
    }

    // Эталон: прозрачная куча как обычный массив. Таблица должна давать те же байты при любой последовательности.
    public static int Run()
    {
        int checks = 0, fails = 0; void C(bool ok, string m) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL PAGETABLE: " + m); } }
        const int P = 4096, N = 64;
        var codec = new Rle();
        using var t = new CompressedPageTable(codec, N, 64L << 20);
        var reference = new byte[N][];
        var va = new byte[N][]; // «виртуальные адреса»: null = не резидентна
        var rng = new Random(99);

        // Содержимое страниц: сжимаемые (блоки одинаковых байтов) и несколько случайных.
        for (int p = 0; p < N; p++)
        {
            reference[p] = new byte[P];
            if (p % 16 == 7) rng.NextBytes(reference[p]);           // несжимаемая
            else for (int k = 0; k < P; k++) reference[p][k] = (byte)(p + k / 256); // сжимаемая
        }

        void Fault(int p)
        {
            var buf = new byte[P];
            t.OnFault(p, buf);
            // при первом обращении куча Zero -> нули; тест пишет эталон в страницу при первом фолте
            if (va[p] == null && IsZero(buf)) { reference[p].CopyTo(buf.AsSpan()); } // эмуляция: приложение записало данные
            va[p] = buf;
        }
        bool IsZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }

        // 1. Трогаем все страницы (пишем данные), всё резидентно.
        for (int p = 0; p < N; p++) Fault(p);
        C(t.Resident == N, "all pages resident after first touch");

        // 2. Вытесняем всё. Резидентными останутся только несжимаемые (Pinned).
        for (int p = 0; p < N; p++) { if (va[p] != null) { t.Evict(p, va[p]); if (t.State(p) != CompressedPageTable.PageState.Pinned) va[p] = null; } }
        C(t.Resident == 0, "no plain-resident after evict-all");
        C(t.Pinned == N / 16, "incompressible pages pinned, not evicted");  // p%16==7 -> 4 из 64
        C(t.Compressed == N - N / 16, "compressible pages compressed");
        // Экономия проверяется именно здесь, пока холодное не возвращено: резидентны только закреплённые.
        C(t.ResidentBytes < t.LogicalBytes / 2, "resident bytes well below logical after compaction");
        C(t.StoreStats().LedgerBytes > 0 && t.StoreStats().LedgerBytes < t.LogicalBytes, "compressed store smaller than logical");

        // 3. Содержимое доступно и идентично после возврата — ключевое свойство прозрачности.
        for (int p = 0; p < N; p++)
        {
            if (va[p] == null) { var buf = new byte[P]; t.OnFault(p, buf); va[p] = buf; }
            C(va[p].AsSpan().SequenceEqual(reference[p]), "page " + p + " identical after decompress");
        }

        // 4. Запись меняет данные, цикл вытеснения/возврата сохраняет новое.
        var np = reference[3]; for (int k = 0; k < P; k++) np[k] = (byte)(k / 128 + 1); np.CopyTo(va[3].AsSpan());
        t.Touch(3); t.Evict(3, va[3]); va[3] = null;
        var rb = new byte[P]; t.OnFault(3, rb);
        C(rb.AsSpan().SequenceEqual(reference[3]), "rewritten page survives evict/restore");

        // 5. LRU: самая давняя резидентная выбирается первой.
        using var t2 = new CompressedPageTable(codec, 8, 8L << 20);
        var z = new byte[P]; for (int k = 0; k < P; k++) z[k] = 42;
        for (int p = 0; p < 4; p++) { var b = new byte[P]; t2.OnFault(p, b); b = (byte[])z.Clone(); }
        t2.Touch(0); // 0 снова горячая
        var cold = t2.ChooseCold(1);
        C(cold.Length == 1 && cold[0] == 1, "LRU picks oldest resident (1), not re-touched 0");

        // 7. Нулевые страницы не занимают хранилище.
        using var t3 = new CompressedPageTable(codec, 4, 1L << 20);
        var zero = new byte[P]; var fb = new byte[P]; t3.OnFault(0, fb); // Zero -> Resident (нули)
        C(t3.Evict(0, zero) == 0 && t3.State(0) == CompressedPageTable.PageState.Zero, "all-zero page costs no storage");

        // 8. Дедуп-режим: одинаковые страницы хранятся один раз → реальная экономия памяти своей кучи.
        {
            const int M = 64;
            var same = new byte[P]; for (int k = 0; k < P; k++) same[k] = (byte)(k / 256); // сжимаемая, одинаковая
            using var plain = new CompressedPageTable(codec, M, 64L << 20, dedup: false);
            using var dedup = new CompressedPageTable(codec, M, 64L << 20, dedup: true);
            foreach (var tbl in new[] { plain, dedup })
                for (int p = 0; p < M; p++) { var b = new byte[P]; tbl.OnFault(p, b); same.CopyTo(b.AsSpan()); tbl.Touch(p); tbl.Evict(p, b); }
            C(plain.Compressed == M && dedup.Compressed == M, "dedup+plain compressed all identical pages");
            long plainLedger = plain.StoreStats().LedgerBytes, dedupLedger = dedup.StoreStats().LedgerBytes;
            C(dedupLedger * 4 < plainLedger, $"dedup ledger {dedupLedger} far below per-page {plainLedger}");
            var info = dedup.DedupInfo();
            C(info is { UniquePages: 1, LiveKeys: M }, "dedup collapses identical pages to one unique");
            bool okAll = true; for (int p = 0; p < M; p++) { var b = new byte[P]; dedup.OnFault(p, b); if (!b.AsSpan().SequenceEqual(same)) okAll = false; dedup.Touch(p); dedup.Evict(p, b); }
            C(okAll, "dedup restores identical pages correctly");

            using var dd = new CompressedPageTable(codec, 4, 8L << 20, dedup: true);
            var pa = new byte[P]; for (int k = 0; k < P; k++) pa[k] = (byte)(k / 256);
            var pb = new byte[P]; for (int k = 0; k < P; k++) pb[k] = (byte)(k / 256 + 1);
            var a = new byte[P]; dd.OnFault(0, a); pa.CopyTo(a.AsSpan()); dd.Touch(0); dd.Evict(0, a);
            var b2 = new byte[P]; dd.OnFault(1, b2); pb.CopyTo(b2.AsSpan()); dd.Touch(1); dd.Evict(1, b2);
            var g0 = new byte[P]; dd.OnFault(0, g0); var g1 = new byte[P]; dd.OnFault(1, g1);
            C(g0.AsSpan().SequenceEqual(pa) && g1.AsSpan().SequenceEqual(pb), "dedup keeps distinct pages distinct");
        }

        Console.WriteLine($"COMPRESSED PAGETABLE {checks} checks; pure engine, NOT native VEH/VirtualProtect");
        return fails;
    }
}
