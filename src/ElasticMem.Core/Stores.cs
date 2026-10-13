using System.IO.Compression;

namespace ElasticMem.Core;

/// <summary>Логическая память с прозрачной упаковкой. Время задаётся снаружи (Now, секунды).</summary>
public interface IMemoryStore
{
    string Name { get; }
    long LogicalSize { get; }
    double Now { get; set; }
    void Read(long offset, Span<byte> dst);
    void Write(long offset, ReadOnlySpan<byte> src);
    /// <summary>Фоновый шаг политики: упаковка остывших блоков.</summary>
    void Tick();
    /// <summary>Вся занятая память: данные + сжатые представления + патчи + метаданные.</summary>
    long Footprint { get; }
    StoreStats Stats { get; }
    IReadOnlyDictionary<string, int> FormatHistogram();
}

public sealed class StoreStats
{
    public long EncodedBytes, DecodedBytes;       // объём обработанных данных (прокси CPU)
    public long Compressions, Promotions, EarlyPromotions, Rebases;
    public long InPlaceWrites, PatchWrites, HotWrites, ColdReads;
}

/// <summary>Экспоненциально затухающий счётчик событий → оценка частоты (событий/с).</summary>
public struct DecayingRate
{
    private double _v, _t;
    public void Add(double now, double tau, double amount = 1)
    {
        _v = Decay(now, tau) + amount;
        _t = now;
    }
    private readonly double Decay(double now, double tau) => _v == 0 ? 0 : _v * Math.Exp(-(now - _t) / tau);
    public readonly double Rate(double now, double tau) => Decay(now, tau) / tau;
}

public sealed class ElasticStoreOptions
{
    public string Name = "Эластичные микроблоки";
    public int BlockSize = 1024;
    public CodecOptions Codec = new();
    public bool EnablePatches = true;
    public bool LifetimeAware = true;   // «компрессор с прогнозом срока жизни представления»
    public double MinDwellSec = 0.5;    // минимальная пауза перед упаковкой
    public double MaxPenaltySec = 20;   // штраф за метания hot↔cold
    public double HorizonSec = 10;      // горизонт прогноза выгоды
    public double RateTauSec = 2;       // окно оценки частот доступа
    public int PatchBudgetDivisor = 8;  // патчи ≤ BlockSize/8 байт
    public int MaxPatches = 8;
    public double CpuWeight = 0.02;     // «байт·с памяти» за 1 обработанный байт
    public double PromoteReadRate = 40; // чтений/с у Lz/патченного блока → распаковать
    public int MetadataBytesPerBlock = 16;
}

/// <summary>
/// Эластичные микроблоки: логическая память делится на блоки, каждый хранится в своём представлении
/// (обычный / нули / битовая упаковка / база+смещения / разреженный / словарный) + ограниченный набор патчей.
/// </summary>
public sealed class ElasticStore : IMemoryStore
{
    private sealed class Patch { public int Off; public byte[] Data = Array.Empty<byte>(); }

    private sealed class Block
    {
        public byte[]? Hot;
        public EncodedBlock? Cold;
        public List<Patch>? Patches;
        public int PatchBytes;
        public double LastAccess, ColdSince, Penalty, NextEval;
        public DecayingRate Reads, Writes;
        public int LastBits = -1;
        public double LastBitsTime, GrowthBitsPerSec;
    }

    private readonly ElasticStoreOptions _o;
    private readonly Block[] _blocks;
    private readonly byte[] _scratch;
    private long _footCold, _hotCount, _patchBytes, _patchCount;

    public string Name => _o.Name;
    public long LogicalSize { get; }
    public double Now { get; set; }
    public StoreStats Stats { get; } = new();
    public ElasticStoreOptions Options => _o;

    public ElasticStore(long size, ElasticStoreOptions? options = null)
    {
        _o = options ?? new ElasticStoreOptions();
        if (_o.BlockSize % 8 != 0) throw new ArgumentException("Размер блока должен быть кратен 8");
        int n = (int)((size + _o.BlockSize - 1) / _o.BlockSize);
        LogicalSize = (long)n * _o.BlockSize;
        _blocks = new Block[n];
        for (int i = 0; i < n; i++) _blocks[i] = new Block { Hot = new byte[_o.BlockSize] };
        _hotCount = n;
        _scratch = new byte[_o.BlockSize];
    }

    public long Footprint => _hotCount * _o.BlockSize + _footCold + _patchBytes + _patchCount * 8 + (long)_blocks.Length * _o.MetadataBytesPerBlock;

    private int PatchBudget => _o.BlockSize / _o.PatchBudgetDivisor;

    public void Read(long offset, Span<byte> dst)
    {
        int bs = _o.BlockSize;
        while (dst.Length > 0)
        {
            int bi = (int)(offset / bs), lo = (int)(offset % bs);
            int len = Math.Min(bs - lo, dst.Length);
            ReadBlock(_blocks[bi], lo, dst[..len]);
            dst = dst[len..];
            offset += len;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> src)
    {
        int bs = _o.BlockSize;
        while (src.Length > 0)
        {
            int bi = (int)(offset / bs), lo = (int)(offset % bs);
            int len = Math.Min(bs - lo, src.Length);
            WriteBlock(_blocks[bi], lo, src[..len]);
            src = src[len..];
            offset += len;
        }
    }

    private void ReadBlock(Block b, int lo, Span<byte> dst)
    {
        b.LastAccess = Now;
        b.Reads.Add(Now, _o.RateTauSec);
        if (b.Hot != null) { b.Hot.AsSpan(lo, dst.Length).CopyTo(dst); return; }

        var e = b.Cold!;
        Stats.ColdReads++;
        if (e.SupportsWordAccess && b.Patches == null)
        {
            // Прямое чтение из упакованного представления — без распаковки блока.
            int w0 = lo / 8, w1 = (lo + dst.Length - 1) / 8;
            Span<byte> tmp = stackalloc byte[8];
            for (int w = w0; w <= w1; w++)
            {
                BitConverter.TryWriteBytes(tmp, BlockCodec.GetWord(e, w));
                int ws = w * 8;
                int from = Math.Max(lo, ws), to = Math.Min(lo + dst.Length, ws + 8);
                tmp[(from - ws)..(to - ws)].CopyTo(dst[(from - lo)..]);
            }
            Stats.DecodedBytes += (w1 - w0 + 1) * 8;
            if (b.Reads.Rate(Now, _o.RateTauSec) > _o.PromoteReadRate * 8) Promote(b);
            return;
        }
        Materialize(b, _scratch);
        _scratch.AsSpan(lo, dst.Length).CopyTo(dst);
        if (b.Reads.Rate(Now, _o.RateTauSec) > _o.PromoteReadRate) Promote(b);
    }

    private void Materialize(Block b, byte[] dst)
    {
        BlockCodec.Decode(b.Cold!, dst);
        Stats.DecodedBytes += _o.BlockSize;
        if (b.Patches != null) foreach (var p in b.Patches) p.Data.CopyTo(dst, p.Off);
    }

    private void WriteBlock(Block b, int lo, ReadOnlySpan<byte> src)
    {
        b.LastAccess = Now;
        b.Writes.Add(Now, _o.RateTauSec);
        if (b.Hot != null) { src.CopyTo(b.Hot.AsSpan(lo)); Stats.HotWrites++; return; }

        var e = b.Cold!;
        if (e.SupportsWordAccess && b.Patches == null && TryWriteInPlace(e, lo, src)) { Stats.InPlaceWrites++; return; }
        if (_o.EnablePatches && TryAddPatch(b, lo, src)) { Stats.PatchWrites++; return; }

        // Патчей слишком много / формат сломан: либо новая основа, либо обратно в несжатый вид.
        bool busy = b.Writes.Rate(Now, _o.RateTauSec) * _o.HorizonSec > _o.MaxPatches;
        Promote(b);
        src.CopyTo(b.Hot.AsSpan(lo));
        Stats.HotWrites++;
        if (!busy && b.Penalty < _o.MaxPenaltySec / 2) { Stats.Rebases++; TryCompress(b, force: true); }
    }

    private bool TryWriteInPlace(EncodedBlock e, int lo, ReadOnlySpan<byte> src)
    {
        int w0 = lo / 8, w1 = (lo + src.Length - 1) / 8;
        int cnt = w1 - w0 + 1;
        Span<ulong> vals = cnt <= 128 ? stackalloc ulong[cnt] : new ulong[cnt];
        Span<byte> tmp = stackalloc byte[8];
        int needExc = 0;
        for (int w = w0; w <= w1; w++)
        {
            int ws = w * 8;
            int from = Math.Max(lo, ws), to = Math.Min(lo + src.Length, ws + 8);
            ulong v;
            if (to - from == 8) v = BitConverter.ToUInt64(src[(from - lo)..]);
            else
            {
                BitConverter.TryWriteBytes(tmp, BlockCodec.GetWord(e, w));
                src[(from - lo)..(to - lo)].CopyTo(tmp[(from - ws)..]);
                v = BitConverter.ToUInt64(tmp);
            }
            vals[w - w0] = v;
            var r = BlockCodec.CanSetWord(e, w, v);
            if (r == BlockCodec.SetResult.Impossible) return false;
            if (r == BlockCodec.SetResult.NeedsException) needExc++;
        }
        int excBefore = e.Exceptions?.Count ?? 0;
        if (needExc > 0 && excBefore + needExc > e.MaxExceptions) return false;
        int before = e.SizeBytes;
        for (int w = w0; w <= w1; w++) BlockCodec.SetWord(e, w, vals[w - w0]);
        _footCold += e.SizeBytes - before;
        return true;
    }

    private bool TryAddPatch(Block b, int lo, ReadOnlySpan<byte> src)
    {
        var list = b.Patches ?? new List<Patch>();
        int start = lo, end = lo + src.Length;
        var overlap = new List<Patch>();
        foreach (var p in list)
            if (p.Off <= end && p.Off + p.Data.Length >= start) // пересекаются или соседние
            {
                overlap.Add(p);
                start = Math.Min(start, p.Off);
                end = Math.Max(end, p.Off + p.Data.Length);
            }
        int removed = overlap.Sum(p => p.Data.Length);
        int newBytes = b.PatchBytes - removed + (end - start);
        int newCount = list.Count - overlap.Count + 1;
        if (newBytes > PatchBudget || newCount > _o.MaxPatches) return false;

        // Повторное изменение того же участка заменяет прошлый патч — без бесконечной истории.
        var data = new byte[end - start];
        foreach (var p in overlap) { p.Data.CopyTo(data, p.Off - start); list.Remove(p); }
        src.CopyTo(data.AsSpan(lo - start));
        list.Add(new Patch { Off = start, Data = data });
        _patchBytes += newBytes - b.PatchBytes;
        _patchCount += newCount - (b.Patches?.Count ?? 0);
        b.PatchBytes = newBytes;
        b.Patches = list;
        return true;
    }

    private void Promote(Block b)
    {
        var hot = new byte[_o.BlockSize];
        Materialize(b, hot);
        _footCold -= b.Cold!.SizeBytes;
        _patchBytes -= b.PatchBytes;
        _patchCount -= b.Patches?.Count ?? 0;
        b.Cold = null; b.Patches = null; b.PatchBytes = 0;
        b.Hot = hot;
        _hotCount++;
        Stats.Promotions++;
        // Штраф за метания: блок понадобился вскоре после упаковки.
        if (Now - b.ColdSince < Math.Max(_o.MinDwellSec * 4, 1))
        {
            Stats.EarlyPromotions++;
            b.Penalty = Math.Min(_o.MaxPenaltySec, Math.Max(_o.MinDwellSec, b.Penalty * 2));
        }
        else b.Penalty *= 0.5;
    }

    public void Tick()
    {
        foreach (var b in _blocks)
        {
            if (b.Hot == null || Now < b.NextEval) continue;
            if (Now - b.LastAccess < _o.MinDwellSec + b.Penalty) continue;
            TryCompress(b, force: false);
        }
    }

    private void TryCompress(Block b, bool force)
    {
        var data = b.Hot!;
        var cands = BlockCodec.Candidates(data, _o.Codec);
        var best = Choose(b, cands);
        if (best.Format == BlockFormat.Raw)
        {
            b.NextEval = Now + Math.Max(_o.MinDwellSec * 4, 2) + b.Penalty;
            return;
        }
        Stats.EncodedBytes += _o.BlockSize * (long)cands.Count;
        var e = BlockCodec.Build(data, best, _o.Codec);
        int bits = BlockCodec.RequiredBits(data);
        if (b.LastBits >= 0 && Now > b.LastBitsTime)
        {
            double g = Math.Max(0, bits - b.LastBits) / (Now - b.LastBitsTime);
            b.GrowthBitsPerSec = 0.6 * b.GrowthBitsPerSec + 0.4 * g;
        }
        b.LastBits = bits; b.LastBitsTime = Now;
        b.Cold = e; b.Hot = null; b.ColdSince = Now;
        _hotCount--;
        _footCold += e.SizeBytes;
        Stats.Compressions++;
    }

    private Candidate Choose(Block b, List<Candidate> cands)
    {
        int bs = _o.BlockSize, meta = 8;
        if (!_o.LifetimeAware)
        {
            var m = cands[0];
            foreach (var c in cands) if (c.SizeBytes < m.SizeBytes) m = c;
            return m.SizeBytes + meta < bs ? m : cands[0];
        }
        double H = _o.HorizonSec, wr = b.Writes.Rate(Now, _o.RateTauSec), rr = b.Reads.Rate(Now, _o.RateTauSec);
        double patchLife = _o.EnablePatches ? _o.MaxPatches : 0.5;
        Candidate best = cands[0];
        double bestScore = 0;
        foreach (var c in cands)
        {
            double saved = bs - c.SizeBytes - meta;
            if (saved <= 0) continue;
            double life, encCost, readCost;
            switch (c.Format)
            {
                case BlockFormat.Zero: life = wr < 1e-3 ? H : patchLife / wr; encCost = bs * 0.2; readCost = 1; break;
                case BlockFormat.BitPack:
                case BlockFormat.BaseDelta:
                    // Чем больше запас разрядности, тем дольше живёт представление при росте значений.
                    double g = b.GrowthBitsPerSec;
                    life = wr < 1e-3 || g < 1e-4 ? H : (c.Headroom + 0.5 + (c.Exceptions == 0 ? 1 : 0)) / g;
                    encCost = bs; readCost = 16; break;
                case BlockFormat.Sparse: life = wr < 1e-3 ? H : patchLife / (wr * 0.5); encCost = bs; readCost = 16; break;
                case BlockFormat.Lz: life = wr < 1e-3 ? H : patchLife / wr; encCost = bs * 4; readCost = bs * 2; break;
                default: continue;
            }
            life = Math.Min(life, H);
            double rebuilds = H / Math.Max(life, 1e-3);
            double score = saved * life - _o.CpuWeight * (encCost * rebuilds + rr * H * readCost);
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    public IReadOnlyDictionary<string, int> FormatHistogram()
    {
        var d = new Dictionary<string, int>();
        foreach (var b in _blocks)
        {
            string k = b.Hot != null ? "Hot (обычный)" : b.Cold!.Format + (b.Patches != null ? "+патчи" : "");
            d[k] = d.GetValueOrDefault(k) + 1;
        }
        return d;
    }
}

/// <summary>Базовая линия: обычное постраничное сжатие (страница 4 КБ, Deflate, распаковка целиком при любом доступе).</summary>
public sealed class PageStore : IMemoryStore
{
    private readonly int _ps;
    private readonly byte[]?[] _hot;
    private readonly byte[]?[] _cold;
    private readonly double[] _last;
    private readonly double _dwell;
    private long _hotCount, _coldBytes;

    public string Name => $"Постраничное сжатие ({_ps / 1024} КБ, Deflate)";
    public long LogicalSize { get; }
    public double Now { get; set; }
    public StoreStats Stats { get; } = new();

    public PageStore(long size, int pageSize = 4096, double dwellSec = 0.5)
    {
        _ps = pageSize; _dwell = dwellSec;
        int n = (int)((size + pageSize - 1) / pageSize);
        LogicalSize = (long)n * pageSize;
        _hot = new byte[]?[n]; _cold = new byte[]?[n]; _last = new double[n];
        for (int i = 0; i < n; i++) _hot[i] = new byte[pageSize];
        _hotCount = n;
    }

    public long Footprint => _hotCount * _ps + _coldBytes + _hot.Length * 16L;

    private byte[] Fault(int i)
    {
        _last[i] = Now;
        if (_hot[i] != null) return _hot[i]!;
        var p = new byte[_ps];
        BlockCodec.LzDecompress(_cold[i]!, p);
        Stats.DecodedBytes += _ps; Stats.Promotions++;
        _coldBytes -= _cold[i]!.Length; _cold[i] = null;
        _hot[i] = p; _hotCount++;
        return p;
    }

    public void Read(long offset, Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            int pi = (int)(offset / _ps), lo = (int)(offset % _ps), len = Math.Min(_ps - lo, dst.Length);
            if (_hot[pi] == null) Stats.ColdReads++;
            Fault(pi).AsSpan(lo, len).CopyTo(dst);
            dst = dst[len..]; offset += len;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> src)
    {
        while (src.Length > 0)
        {
            int pi = (int)(offset / _ps), lo = (int)(offset % _ps), len = Math.Min(_ps - lo, src.Length);
            src[..len].CopyTo(Fault(pi).AsSpan(lo));
            Stats.HotWrites++;
            src = src[len..]; offset += len;
        }
    }

    public void Tick()
    {
        for (int i = 0; i < _hot.Length; i++)
        {
            if (_hot[i] == null || Now - _last[i] < _dwell) continue;
            var c = BlockCodec.LzCompress(_hot[i], CompressionLevel.Fastest);
            Stats.EncodedBytes += _ps;
            if (c.Length + 8 >= _ps) { _last[i] = Now; continue; }
            _cold[i] = c; _coldBytes += c.Length; _hot[i] = null; _hotCount--;
            Stats.Compressions++;
        }
    }

    public IReadOnlyDictionary<string, int> FormatHistogram()
    {
        int h = _hot.Count(x => x != null);
        return new Dictionary<string, int> { ["Hot (обычный)"] = h, ["Lz"] = _hot.Length - h };
    }
}
