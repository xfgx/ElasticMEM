using ElasticMem.Core.Research;

static class DedupStoreTests
{
    public static int Run()
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

        var rnd = new Random(3);
        byte[] A = new byte[4096], B = new byte[4096];
        rnd.NextBytes(A); rnd.NextBytes(B);

        var s = new DedupStore();
        long a1 = s.Put(A);
        long a2 = s.Put((byte[])A.Clone());   // тот же контент
        long b1 = s.Put(B);
        Check(a1 == a2, "одинаковый контент → один id");
        Check(a1 != b1, "разный контент → разные id");
        Check(s.UniqueChunks == 2, "уникальных ровно 2");
        Check(s.TotalPuts == 3 && s.LogicalBytes == 3L * 4096, "логический объём учитывает дубликаты");
        Check(s.RefCount(a1) == 2, "refcount повторного Put");

        Check(s.Get(a1).AsSpan().SequenceEqual(A), "Get восстанавливает точные байты A");
        Check(s.Get(b1).AsSpan().SequenceEqual(B), "Get восстанавливает точные байты B");

        s.Release(a1);
        Check(s.RefCount(a1) == 1 && s.UniqueChunks == 2, "один Release не освобождает при refcount>0");
        s.Release(a2);
        Check(s.RefCount(a1) == 0 && s.UniqueChunks == 1, "второй Release освобождает чанк");

        // несжимаемые данные хранятся сырьём, но Get всё равно точен
        var noise = new byte[777]; rnd.NextBytes(noise);
        long n = s.Put(noise);
        Check(s.Get(n).AsSpan().SequenceEqual(noise), "несжимаемый чанк восстанавливается точно");

        // нулевой чанк хорошо жмётся, StoredBytes < Logical
        var zeros = new byte[4096];
        var s2 = new DedupStore();
        for (int i = 0; i < 10; i++) s2.Put(zeros);
        Check(s2.UniqueChunks == 1, "10 одинаковых нулевых страниц → 1 уникальная");
        Check(s2.StoredBytes < 4096, "нулевая страница хранится сжатой");

        // анализатор: на дублирующихся страницах экономия положительная
        var pages = new List<byte[]>();
        var baseP = new byte[4096]; rnd.NextBytes(baseP);
        for (int i = 0; i < 50; i++) pages.Add((byte[])baseP.Clone());
        var rep = DedupAnalyzer.Measure(pages);
        Check(rep.Results[0].SavingVsPerPagePct > 50, "анализатор видит экономию на полных дубликатах");

        Console.WriteLine($"DEDUP STORE {13 - fails}/13 checks ok (SHA-256 content-addressing, Deflate, refcount)");
        return fails;
    }
}
