using ElasticMem.Core.Research;

static class CompressionProofTests
{
    // «Врущий» кодек: сжимает в 8 байт, но распаковывает в нули. Проверка обязана это поймать.
    sealed class LyingCodec : IPageCodec
    {
        public string Name => "lying";
        public byte[] Encode(byte[] src) => new byte[8];
        public byte[] Decode(byte[] src, int length) => new byte[length];
        public void Dispose() { }
    }

    public static int Run()
    {
        int checks = 0, fails = 0;
        void C(bool ok, string what) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL PROOF: " + what); } }
        var rnd = new Random(11);

        // Реалистичная смесь: структуры с указателями, текст, небольшие числа, повторы.
        var pages = new List<byte[]>();
        for (int i = 0; i < 300; i++)
        {
            var p = new byte[4096];
            switch (i % 4)
            {
                case 0: for (int k = 0; k < 4096; k += 8) BitConverter.TryWriteBytes(p.AsSpan(k), 0x00007FF6_1234_0000UL + (ulong)rnd.Next(4096) * 16); break;
                case 1: for (int k = 0; k < 4096; k++) p[k] = (byte)"function render(props){return <div/>}"[k % 37]; break;
                case 2: for (int k = 0; k < 4096; k += 4) BitConverter.TryWriteBytes(p.AsSpan(k), rnd.Next(1000)); break;
                case 3: for (int k = 0; k < 4096; k++) p[k] = (byte)(k / 64 + i); break;
            }
            pages.Add(p);
        }

        // 1. Deflate без потерь и сжимает реалистичные данные.
        foreach (var kind in new[] { "deflate-fast", "deflate", "deflate-max" })
        {
            using var codec = new PortablePageCodec(kind);
            var r = CompressionProof.Run(pages, codec);
            C(r.IntegrityOk == pages.Count && r.IntegrityBad == 0, $"{kind}: все страницы восстановлены побайтово");
            C(r.NegativeControlDetected, $"{kind}: отрицательный контроль поймал порчу");
            C(r.Proven, $"{kind}: доказано");
            C(r.RatioPercent < 60, $"{kind}: реально сжимает ({r.RatioPercent:0.0}%)");
        }

        // 2. Честный учёт: арена = индекс (8 Б/стр.) + все нагрузки.
        using (var codec = new PortablePageCodec("deflate"))
        {
            var packed = CompressionProof.Compress(pages, codec);
            long expected = (long)pages.Count * CompressionProof.IndexEntryBytes + packed.Payloads.Sum(x => (long)x.Length);
            C(packed.ArenaBytes == expected, "размер арены включает индекс и все нагрузки");
        }

        // 3. Несжимаемые (случайные) страницы: fallback в сырьё, коэффициент ≥100% (индекс тоже считается).
        var noise = new List<byte[]>();
        for (int i = 0; i < 64; i++) { var p = new byte[4096]; rnd.NextBytes(p); noise.Add(p); }
        using (var codec = new PortablePageCodec("deflate"))
        {
            var r = CompressionProof.Run(noise, codec);
            C(r.RawFallbackPages == noise.Count, "случайные страницы хранятся сырьём");
            C(r.RatioPercent >= 100, $"на шуме нет фиктивной экономии ({r.RatioPercent:0.00}%)");
            C(r.Proven, "даже сырые страницы проходят проверку целостности и отрицательный контроль");
        }

        // 4. Проверка ловит сломанный кодек: «сжал в 8 байт», но данные потеряны.
        {
            using var codec = new LyingCodec();
            var r = CompressionProof.Run(pages, codec);
            C(r.IntegrityBad == pages.Count, "врущий кодек: все страницы признаны повреждёнными");
            C(!r.Proven, "врущий кодек НЕ доказан, несмотря на 'отличное' сжатие");
        }

        // 5. Порча в реальной арене обнаруживается при полной проверке.
        using (var codec = new PortablePageCodec("deflate"))
        {
            var packed = CompressionProof.Compress(pages, codec);
            var arena = new byte[packed.ArenaBytes];
            CompressionProof.WriteArena(arena, packed);
            arena[arena.Length - 10] ^= 0x5A;
            var (ok, bad, _) = CompressionProof.Verify(arena, packed, codec);
            C(bad >= 1 && ok == pages.Count - bad, "порча одного байта в арене обнаружена при проверке");
        }

        Console.WriteLine($"COMPRESSION PROOF {checks} checks; SHA-256 integrity, negative control, honest arena accounting");
        return fails;
    }
}
