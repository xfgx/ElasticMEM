using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ElasticMem.Core;

namespace ElasticMem.App;

/// <summary>
/// Анализ реальной памяти процесса (только чтение): насколько её можно ужать микроблоками
/// по сравнению с постраничным сжатием, и сколько представлений «переживут» изменения за N секунд.
/// </summary>
public static class ProcessAnalyzer
{
    private struct BlockInfo
    {
        public long Addr; public ulong Hash; public BlockFormat Fmt;
        public ulong Base; public byte Bits, BitsH; public bool Read2;
    }

    private const int Headroom = 4;

    public static string Analyze(int pid, int blockSize, long limitBytes, double recheckSec, IProgress<string> progress, CancellationToken ct)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return $"Не удалось открыть процесс {pid} (ошибка {Marshal.GetLastWin32Error()}). Запустите программу от имени администратора.";
        try
        {
            var regions = new List<(long addr, long size)>();
            long addr = 0, total = 0;
            int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
            while (total < limitBytes && Native.VirtualQueryEx(h, (IntPtr)addr, out var mbi, (IntPtr)mbiSize) != IntPtr.Zero)
            {
                long size = (long)(ulong)mbi.RegionSize;
                if (mbi.State == Native.MEM_COMMIT && mbi.Type == Native.MEM_PRIVATE &&
                    (mbi.Protect & Native.ReadableMask) != 0 && (mbi.Protect & Native.PAGE_GUARD) == 0)
                {
                    long take = Math.Min(size, limitBytes - total);
                    regions.Add(((long)mbi.BaseAddress, take));
                    total += take;
                }
                long next = (long)mbi.BaseAddress + size;
                if (next <= addr) break;
                addr = next;
            }

            var opt = new CodecOptions();
            var optH = new CodecOptions { Headrooms = new[] { Headroom } };
            var blocks = new List<BlockInfo>((int)(total / blockSize) + 1);
            long scanned = 0, pageSize = 0, microSize = 0;
            var fmtCount = new Dictionary<BlockFormat, (long n, long bytes)>();
            var chunk = new byte[1 << 20];
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var (ra, rs) in regions)
            {
                for (long off = 0; off < rs; off += chunk.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(chunk.Length, rs - off);
                    if (!ReadPages(h, ra + off, chunk, len, out int got)) continue;
                    for (int p = 0; p + 4096 <= got; p += 4096)
                    {
                        var page = chunk.AsSpan(p, 4096);
                        pageSize += Math.Min(4096, BlockCodec.LzCompress(page, CompressionLevel.Fastest).Length + 16);
                    }
                    for (int b = 0; b + blockSize <= got; b += blockSize)
                    {
                        var blk = chunk.AsSpan(b, blockSize);
                        var cands = BlockCodec.Candidates(blk, opt);
                        var best = cands.MinBy(c => c.SizeBytes);
                        microSize += best.SizeBytes + 16;
                        var fc = fmtCount.GetValueOrDefault(best.Format);
                        fmtCount[best.Format] = (fc.n + 1, fc.bytes + best.SizeBytes);
                        var bi = new BlockInfo { Addr = ra + off + b, Hash = Fnv(blk), Fmt = best.Format, Base = best.Base, Bits = (byte)best.Bits };
                        if (best.Format is BlockFormat.BitPack or BlockFormat.BaseDelta)
                            bi.BitsH = (byte)Math.Min(63, best.Bits + Headroom);
                        blocks.Add(bi);
                        scanned += blockSize;
                    }
                    progress.Report($"Снимок 1: {scanned >> 20} / {total >> 20} МБ");
                }
            }
            double scanSec = sw.Elapsed.TotalSeconds;
            if (scanned == 0) return "Нет доступной для чтения приватной памяти.";

            var sb = new StringBuilder();
            sb.AppendLine($"Процесс PID {pid}: просканировано {scanned / 1048576.0:F1} МБ приватной памяти ({regions.Count} регионов) за {scanSec:F1} с");
            sb.AppendLine($"Размер микроблока: {blockSize} Б (экспериментальный параметр)");
            sb.AppendLine();
            sb.AppendLine($"  Постраничное сжатие 4 КБ (Deflate):   {pageSize / 1048576.0,8:F1} МБ  ({100.0 * pageSize / scanned:F1}% от исходного)");
            sb.AppendLine($"  Адаптивные микроблоки (+метаданные):  {microSize / 1048576.0,8:F1} МБ  ({100.0 * microSize / scanned:F1}% от исходного)");
            sb.AppendLine();
            sb.AppendLine("  Выбранные форматы:");
            foreach (var kv in fmtCount.OrderByDescending(k => k.Value.n))
                sb.AppendLine($"    {Describe(kv.Key),-44} {kv.Value.n,8} блоков  {100.0 * kv.Value.n / blocks.Count,5:F1}%   → {kv.Value.bytes / 1048576.0:F2} МБ");

            if (recheckSec > 0)
            {
                for (double t = recheckSec; t > 0; t -= 1) { ct.ThrowIfCancellationRequested(); progress.Report($"Ожидание изменений: {t:F0} с"); Thread.Sleep(1000); }
                long changed = 0, aliveGreedy = 0, aliveHead = 0, packedChanged = 0, gone = 0;
                var buf = new byte[blockSize];
                var span = CollectionsMarshal.AsSpan(blocks);
                for (int i = 0; i < span.Length; i++)
                {
                    if ((i & 4095) == 0) { ct.ThrowIfCancellationRequested(); progress.Report($"Снимок 2: {i * (long)blockSize >> 20} МБ"); }
                    ref var bi = ref span[i];
                    if (!ReadPages(h, bi.Addr, buf, blockSize, out int got) || got < blockSize) { gone++; continue; }
                    if (Fnv(buf) == bi.Hash) continue;
                    changed++;
                    if (bi.Fmt is BlockFormat.BitPack or BlockFormat.BaseDelta)
                    {
                        packedChanged++;
                        var w = MemoryMarshal.Cast<byte, ulong>(buf.AsSpan());
                        ulong limG = BitPack.MaxValue(bi.Bits), limH = BitPack.MaxValue(bi.BitsH);
                        bool g = true, hh = true;
                        foreach (var v in w)
                        {
                            if (v < bi.Base) { g = hh = false; break; }
                            if (v - bi.Base > limG) g = false;
                            if (v - bi.Base > limH) { hh = false; break; }
                        }
                        if (g) aliveGreedy++;
                        if (hh) aliveHead++;
                    }
                }
                sb.AppendLine();
                sb.AppendLine($"  Через {recheckSec:F0} с: изменилось {changed} блоков из {blocks.Count} ({100.0 * changed / blocks.Count:F1}%), освобождено/недоступно {gone}");
                if (packedChanged > 0)
                {
                    sb.AppendLine($"  Из изменившихся упакованных (BitPack/BaseDelta) блоков: {packedChanged}");
                    sb.AppendLine($"    представление осталось валидным без перепаковки (плотная упаковка):      {100.0 * aliveGreedy / packedChanged,5:F1}%");
                    sb.AppendLine($"    представление осталось валидным с запасом +{Headroom} бита (прогноз срока жизни): {100.0 * aliveHead / packedChanged,5:F1}%");
                }
                sb.AppendLine("  Остальные изменившиеся блоки потребовали бы патча или новой основы.");
            }
            sb.AppendLine();
            sb.AppendLine("Примечание: это измерение (только чтение). Обычное приложение не умеет читать упакованные блоки напрямую —");
            sb.AppendLine("для реальной экономии используйте агент (сжатие в простое средствами Windows) или рантайм ElasticStore в своём приложении.");
            return sb.ToString();
        }
        finally { Native.CloseHandle(h); }
    }

    private static bool ReadPages(IntPtr h, long addr, byte[] buf, int len, out int got)
    {
        got = 0;
        if (Native.ReadProcessMemory(h, (IntPtr)addr, buf, (IntPtr)len, out var r) && (long)r == len) { got = len; return true; }
        // частичное чтение: по страницам, до первой недоступной
        var page = new byte[4096];
        for (int p = 0; p < len; p += 4096)
        {
            int l = Math.Min(4096, len - p);
            if (!Native.ReadProcessMemory(h, (IntPtr)(addr + p), page, (IntPtr)l, out r) || (long)r != l) break;
            Buffer.BlockCopy(page, 0, buf, p, l);
            got += l;
        }
        return got > 0;
    }

    private static ulong Fnv(ReadOnlySpan<byte> s)
    {
        ulong hsh = 14695981039346656037UL;
        foreach (var w in MemoryMarshal.Cast<byte, ulong>(s)) { hsh ^= w; hsh *= 1099511628211UL; }
        return hsh;
    }

    public static string Describe(BlockFormat f) => f switch
    {
        BlockFormat.Raw => "Raw — без сжатия (непредсказуемые байты)",
        BlockFormat.Zero => "Zero — блок из нулей",
        BlockFormat.BitPack => "BitPack — узкие числа",
        BlockFormat.BaseDelta => "BaseDelta — база + смещения (+исключения)",
        BlockFormat.Sparse => "Sparse — маска ненулевых + значения",
        BlockFormat.Lz => "Lz — словарное сжатие",
        _ => f.ToString(),
    };
}
