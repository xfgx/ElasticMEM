using ElasticMem.Core.Research;
static class CodecBakeoffTests
{
    public static int Run()
    {
        int checks = 0, fails = 0; void C(bool ok, string m) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL BAKEOFF: " + m); } }
        var rng = new Random(5); var pages = new List<byte[]>();
        var words = "message chat user online typing http://example.com status ok error renderer frame layer".Split(' ');
        for (int i = 0; i < 1200; i++)
        {
            var p = new byte[4096];
            switch (i % 6)
            {
                case 0: break;                                                            // нулевая
                case 1: { var s = System.Text.Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Range(0, 600).Select(_ => words[rng.Next(words.Length)]))); s.AsSpan(0, Math.Min(4096, s.Length)).CopyTo(p); break; } // текст
                case 2: for (int k = 0; k < 512; k++) BitConverter.TryWriteBytes(p.AsSpan(k * 8), 0x7ff6_0000_0000L + rng.Next(1 << 20) * 16L); break; // указатели
                case 3: for (int k = 0; k < 1024; k++) BitConverter.TryWriteBytes(p.AsSpan(k * 4), rng.Next(300)); break; // малые числа
                case 4: rng.NextBytes(p); break;                                          // несжимаемое (медиа)
                case 5: Array.Fill(p, (byte)0xCD); break;                                 // одинаковые страницы -> дубликаты
            }
            pages.Add(p);
        }
        var r = CodecBakeoff.Run(pages, windowsCodecs: false);
        C(r.Error == null, "no error: " + r.Error);
        C(r.ZeroPages == 200, "zero pages counted separately (" + r.ZeroPages + ")");
        C(r.TestPages > 16, "test half non-empty");
        C(r.DuplicatePages > 0, "exact duplicate pages detected");
        foreach (var row in r.Rows) C(row.RatioPercent is > 0 and <= 100.0001, row.Codec + " ratio in (0,100]: " + row.RatioPercent);
        C(r.Rows.Any(x => x.Codec.StartsWith("BΔI")), "our BΔI codec measured");
        C(r.Rows.Any(x => x.Codec.Contains("словарь")), "dictionary codec measured");
        var lz4 = r.Rows.First(x => x.Codec == "lz4 4K"); var zd = r.Rows.FirstOrDefault(x => x.Codec == "zstd-1 4K + словарь" && x.Pages > 0);
        if (zd != null) C(zd.RatioPercent <= lz4.RatioPercent, "dictionary not worse than lz4 on same data");
        Console.WriteLine($"CODEC BAKEOFF {checks} checks; portable codecs on synthetic pages, NOT real app pages / XPRESS");
        foreach (var row in r.Rows) Console.WriteLine($"   {row.Codec,-22} {row.RatioPercent,6:F1}%  dec p50 {row.DecodeP50Us,6:F1} us");
        return fails;
    }
}
