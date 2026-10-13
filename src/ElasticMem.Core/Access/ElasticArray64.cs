using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ElasticMem.Core.Access;

/// <summary>Представление чанка (512 слов = 4 КБ логически).</summary>
public enum Rep : byte
{
    Raw = 0,     // обычный массив — частые случайные записи по всему объёму
    Packed = 1,  // база + смещения минимальной разрядности — редкие случайные чтения, минимум байт
    Aligned = 2, // база + смещения 8/16/32 бита — последовательное чтение с векторным декодированием
    Sparse = 3,  // битовая маска + ненулевые значения
    Dense = 4,   // Deflate — долгое отсутствие обращений
}

public sealed class ElasticArrayOptions
{
    public string Name = "ElasticArray64 (по способу доступа)";
    public long BudgetBytes = long.MaxValue; // бюджет RAM на всю структуру
    public bool AccessAware = true;          // false = выбор только по содержимому (самый маленький формат)
    public int OverlayCapacity = 32;         // слой изменений: записей на чанк (степень двойки)
    public int MaxProbe = 4;                 // без длинных цепочек поиска
    public int MaxHotLines = 2;              // горячее окно: до 2 строк по 64 слова на чанк
    public long HotWindowBudgetBytes = 4 << 20;
    public int MinDwellEpochs = 2;           // гистерезис
    public double HorizonEpochs = 16;        // амортизация стоимости перехода
    public int DenseAfterIdleEpochs = 3;
    public CostModel? Costs;
}

/// <summary>Стоимости операций (нс), измеряются на этой машине (Calibrate).</summary>
public sealed class CostModel
{
    public double[] RandRead = new double[5], SeqElem = new double[5], WriteFit = new double[5];
    public double OverlayWrite, EncodeChunk, DenseEncodeChunk, MaterializeChunk, DenseDecodeChunk;
    /// <summary>Типичные значения для x64 (используются до калибровки).</summary>
    public static CostModel Default { get; } = new()
    {
        RandRead = new double[] { 6, 9, 8, 12, 400 }, SeqElem = new double[] { 0.4, 1.2, 0.3, 0.8, 2.5 },
        WriteFit = new double[] { 6, 12, 9, 14, 0 }, OverlayWrite = 25, EncodeChunk = 2500, DenseEncodeChunk = 30000,
        MaterializeChunk = 1500, DenseDecodeChunk = 6000,
    };

    public override string ToString()
    {
        string R(double[] a) => string.Join(" ", Enum.GetValues<Rep>().Select(r => $"{r}={a[(int)r]:F1}"));
        return $"случайное чтение, нс: {R(RandRead)}\nпоследовательное, нс/элемент: {R(SeqElem)}\nзапись (влезает), нс: {R(WriteFit)}\n" +
               $"запись в слой изменений: {OverlayWrite:F1} нс; упаковка чанка: {EncodeChunk:F0} нс, Deflate: {DenseEncodeChunk:F0} нс; " +
               $"распаковка чанка: {MaterializeChunk:F0} нс, Deflate: {DenseDecodeChunk:F0} нс";
    }
}

public sealed class ArrayStats
{
    public long OverlayWrites, InPlaceWrites, HotLinePromotions, RawPromotions, Merges, Transitions, DenseDecodes;
    public long[] ChunksByRep = new long[5];
    public bool BudgetMet = true;
}

/// <summary>
/// Массив 64-битных чисел со сжатой основой и горячим окном.
/// Чтение одного элемента извлекает только его (без распаковки блока); частые записи идут в индексированный
/// слой изменений и маленькое несжатое окно; после затихания изменения сливаются с основой.
/// Представление каждого чанка выбирается по способу доступа при заданном бюджете RAM.
/// </summary>
public sealed class ElasticArray64
{
    public const int ChunkShift = 9, ChunkWords = 1 << ChunkShift, LineShift = 6, LineWords = 1 << LineShift;
    private const int MetaBytes = 48;

    private sealed class Chunk
    {
        public Rep Rep;
        public ulong[]? Raw;
        public byte[]? Pay; public ulong Base; public int Bits; public ulong Mask; // Packed / Aligned
        public ulong[]? Bitmap; public ushort[]? Prefix; public ulong[]? SpVals;  // Sparse
        public byte[]? Lz;                                                        // Dense
        public ushort[]? OvKey; public ulong[]? OvVal; public int OvCount;        // слой изменений
        public ulong[]?[]? Hot; public int[]? HotIdx; public int HotCount;         // горячее окно
        // статистика
        public int RandReads, SeqElems, Writes, NonFit; public byte LinesWritten;
        public double ERand, ESeq, EWrite, ENonFit, ESpread;
        public int EpochsInRep, IdleEpochs;
        public ulong CMin, CMax; public int Nz; public int LzSize = -1; public bool ContentStale = true;
        public bool Dirty => OvKey != null || HotCount > 0;
    }

    /// <summary>Горячие метаданные чанка — плотный массив структур (как таблица метаданных в контроллере памяти).</summary>
    private struct Meta { public object? Data; public ulong Base, Mask; public byte Mode, Bits; }
    private const byte SlowMode = 0x80; // есть слой изменений / горячие строки → медленный путь

    private readonly Chunk[] _c;
    private readonly Meta[] _m;
    private uint _tick; // выборочный подсчёт чтений (каждое 8-е), чтобы статистика не стоила как само чтение
    private readonly int[] _rr, _sq, _wr, _nf;
    private readonly byte[] _lw;
    private readonly ElasticArrayOptions _o;
    private readonly CostModel _k;
    private long _hotBytes;
    private readonly ulong[] _scratch = new ulong[ChunkWords];
    // кеш распакованных Dense-чанков (маленький, независимо декодируемые блоки)
    private readonly int[] _dcKey = { -1, -1, -1, -1, -1, -1, -1, -1 };
    private readonly ulong[][] _dcVal = Enumerable.Range(0, 8).Select(_ => new ulong[ChunkWords]).ToArray();
    private int _dcNext;

    public long Length { get; }
    public string Name => _o.Name;
    public ArrayStats Stats { get; } = new();
    public ElasticArrayOptions Options => _o;

    public ElasticArray64(long length, ElasticArrayOptions? o = null)
    {
        _o = o ?? new ElasticArrayOptions();
        _k = _o.Costs ?? CostModel.Default;
        Length = length;
        int n = (int)((length + ChunkWords - 1) >> ChunkShift);
        _c = new Chunk[n];
        _m = new Meta[n];
        _rr = new int[n]; _sq = new int[n]; _wr = new int[n]; _nf = new int[n]; _lw = new byte[n];
        for (int i = 0; i < n; i++) { _c[i] = new Chunk { Rep = Rep.Raw, Raw = new ulong[ChunkWords] }; Sync(i); }
    }

    private void Sync(int ci)
    {
        var c = _c[ci];
        ref var m = ref _m[ci];
        m.Base = c.Base; m.Mask = c.Mask; m.Bits = (byte)c.Bits;
        m.Mode = (byte)((byte)c.Rep | (c.Dirty ? SlowMode : 0));
        m.Data = c.Rep == Rep.Raw ? c.Raw : c.Pay;
    }

    // ───────────────────────────── чтение ─────────────────────────────

    public ulong this[long i]
    {
        get => Get(i);
        set => Set(i, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Get(long i)
    {
        int ci = (int)(i >> ChunkShift);
        int j = (int)i & (ChunkWords - 1);
        if ((++_tick & 7) == 0) _rr[ci] += 8;
        ref var m = ref _m[ci];
        // быстрый путь: без проверок границ и без обращения к объекту чанка — одно чтение данных
        switch (m.Mode)
        {
            case (byte)Rep.Raw:
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.As<ulong[]>(m.Data!)), j);
            case (byte)Rep.Packed:
            {
                int bit = j * m.Bits;
                ref byte p = ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(m.Data!));
                return m.Base + ((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, bit >> 3)) >> (bit & 7)) & m.Mask);
            }
            case (byte)Rep.Aligned:
            {
                ref byte p = ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(m.Data!));
                return m.Base + (m.Bits == 16 ? Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, j * 2))
                               : m.Bits == 8 ? Unsafe.Add(ref p, j) : (ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, j * 4)));
            }
        }
        return GetSlow(ci, j);
    }

    private ulong GetSlow(int ci, int j)
    {
        var c = _c[ci];
        if (c.HotCount > 0)
        {
            int line = j >> LineShift;
            for (int k = 0; k < c.HotCount; k++) if (c.HotIdx![k] == line) return c.Hot![k]![j & (LineWords - 1)];
        }
        if (c.OvKey != null && OvGet(c, j, out ulong ov)) return ov;
        return BaseGet(c, ci, j);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong BaseGet(Chunk c, int ci, int j)
    {
        switch (c.Rep)
        {
            case Rep.Packed:
            {
                // значение = база + смещение[i]; одно невыровненное 8-байтовое чтение
                int bit = j * c.Bits;
                ulong w = BinaryPrimitives.ReadUInt64LittleEndian(c.Pay.AsSpan(bit >> 3));
                return c.Base + ((w >> (bit & 7)) & c.Mask);
            }
            case Rep.Aligned:
                return c.Bits switch
                {
                    8 => c.Base + c.Pay![j],
                    16 => c.Base + Unsafe.ReadUnaligned<ushort>(ref c.Pay![j * 2]),
                    _ => c.Base + Unsafe.ReadUnaligned<uint>(ref c.Pay![j * 4]),
                };
            case Rep.Sparse:
            {
                int w = j >> 6;
                ulong bm = c.Bitmap![w], b = 1UL << (j & 63);
                if ((bm & b) == 0) return 0;
                return c.SpVals![c.Prefix![w] + BitOperations.PopCount(bm & (b - 1))];
            }
            case Rep.Dense:
                return DenseView(c, ci)[j];
            default:
                return c.Raw![j];
        }
    }

    private ulong[] DenseView(Chunk c, int ci)
    {
        for (int k = 0; k < _dcKey.Length; k++) if (_dcKey[k] == ci) return _dcVal[k];
        int slot = _dcNext++ & (_dcKey.Length - 1);
        LzDecode(c.Lz!, _dcVal[slot]);
        _dcKey[slot] = ci;
        Stats.DenseDecodes++;
        return _dcVal[slot];
    }

    private void DenseInvalidate(int ci)
    {
        for (int k = 0; k < _dcKey.Length; k++) if (_dcKey[k] == ci) _dcKey[k] = -1;
    }

    /// <summary>Последовательное чтение диапазона в буфер.</summary>
    public void ReadRange(long start, Span<ulong> dst)
    {
        long i = start;
        while (dst.Length > 0)
        {
            int ci = (int)(i >> ChunkShift), j = (int)(i & (ChunkWords - 1));
            int len = Math.Min(ChunkWords - j, dst.Length);
            var c = _c[ci];
            _sq[ci] += len;
            if (c.Rep == Rep.Raw) c.Raw.AsSpan(j, len).CopyTo(dst);
            else if (len == ChunkWords && !c.Dirty) DecodeBase(c, ci, dst[..len]);
            else { Materialize(c, ci, _scratch); _scratch.AsSpan(j, len).CopyTo(dst); }
            dst = dst[len..];
            i += len;
        }
    }

    /// <summary>Сумма диапазона — агрегат прямо по сжатому представлению (векторно для 8/16/32 бит).</summary>
    public ulong Sum(long start, long count)
    {
        ulong s = 0;
        long i = start, end = start + count;
        while (i < end)
        {
            int ci = (int)(i >> ChunkShift), j = (int)(i & (ChunkWords - 1));
            int len = (int)Math.Min(ChunkWords - j, end - i);
            _sq[ci] += len;
            if (len == ChunkWords && _m[ci].Mode < SlowMode)
            {
                ref var m = ref _m[ci];
                if (m.Mode == (byte)Rep.Raw) { s += Kernels.SumU64(Unsafe.As<ulong[]>(m.Data!)); i += len; continue; }
                if (m.Mode == (byte)Rep.Aligned && m.Bits == 16)
                {
                    s += m.Base * ChunkWords + Kernels.SumU16(MemoryMarshal.Cast<byte, ushort>(Unsafe.As<byte[]>(m.Data!).AsSpan(0, ChunkWords * 2)));
                    i += len; continue;
                }
            }
            var c = _c[ci];
            if (len == ChunkWords && !c.Dirty) s += SumChunk(c, ci);
            else if (c.Rep == Rep.Raw) s += Kernels.SumU64(c.Raw.AsSpan(j, len));
            else { Materialize(c, ci, _scratch); s += Kernels.SumU64(_scratch.AsSpan(j, len)); }
            i += len;
        }
        return s;
    }

    private ulong SumChunk(Chunk c, int ci)
    {
        switch (c.Rep)
        {
            case Rep.Raw: return Kernels.SumU64(c.Raw);
            case Rep.Aligned:
                return c.Base * ChunkWords + c.Bits switch
                {
                    8 => Kernels.SumU8(c.Pay.AsSpan(0, ChunkWords)),
                    16 => Kernels.SumU16(MemoryMarshal.Cast<byte, ushort>(c.Pay.AsSpan(0, ChunkWords * 2))),
                    _ => Kernels.SumU32(MemoryMarshal.Cast<byte, uint>(c.Pay.AsSpan(0, ChunkWords * 4))),
                };
            case Rep.Packed: return c.Base * ChunkWords + Kernels.SumPacked(c.Pay!, c.Bits, ChunkWords);
            case Rep.Sparse: return Kernels.SumU64(c.SpVals);
            default: return Kernels.SumU64(DenseView(c, ci));
        }
    }

    private void DecodeBase(Chunk c, int ci, Span<ulong> dst)
    {
        switch (c.Rep)
        {
            case Rep.Raw: c.Raw.AsSpan().CopyTo(dst); break;
            case Rep.Aligned:
                if (c.Bits == 8) Kernels.WidenU8(c.Pay.AsSpan(0, ChunkWords), c.Base, dst);
                else if (c.Bits == 16) Kernels.WidenU16(MemoryMarshal.Cast<byte, ushort>(c.Pay.AsSpan(0, ChunkWords * 2)), c.Base, dst);
                else Kernels.WidenU32(MemoryMarshal.Cast<byte, uint>(c.Pay.AsSpan(0, ChunkWords * 4)), c.Base, dst);
                break;
            case Rep.Packed:
                for (int j = 0; j < ChunkWords; j++)
                {
                    int bit = j * c.Bits;
                    dst[j] = c.Base + ((BinaryPrimitives.ReadUInt64LittleEndian(c.Pay.AsSpan(bit >> 3)) >> (bit & 7)) & c.Mask);
                }
                break;
            case Rep.Sparse:
            {
                dst[..ChunkWords].Clear();
                int r = 0;
                for (int w = 0; w < ChunkWords / 64; w++)
                {
                    ulong bm = c.Bitmap![w];
                    while (bm != 0) { int b = BitOperations.TrailingZeroCount(bm); dst[w * 64 + b] = c.SpVals![r++]; bm &= bm - 1; }
                }
                break;
            }
            case Rep.Dense: DenseView(c, ci).AsSpan().CopyTo(dst); break;
        }
    }

    private void Materialize(Chunk c, int ci, ulong[] dst)
    {
        DecodeBase(c, ci, dst);
        if (c.OvKey != null)
            for (int k = 0; k < c.OvKey!.Length; k++) if (c.OvKey[k] != 0 && c.OvKey[k] != Tomb) dst[c.OvKey[k] - 1] = c.OvVal![k];
        for (int k = 0; k < c.HotCount; k++) c.Hot![k]!.CopyTo(dst, c.HotIdx![k] << LineShift);
    }

    // ───────────────────────────── запись ─────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(long i, ulong v)
    {
        int ci = (int)(i >> ChunkShift);
        int j = (int)i & (ChunkWords - 1);
        _wr[ci]++;
        _lw[ci] |= (byte)(1 << (j >> LineShift));
        ref var m = ref _m[ci];
        if (m.Mode == (byte)Rep.Raw)
        {
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.As<ulong[]>(m.Data!)), j) = v;
            return;
        }
        ulong dlt = v - m.Base;
        if (m.Mode == (byte)Rep.Packed && v >= m.Base && dlt <= m.Mask)
        {
            // запись прямо в упакованную основу
            int bit = j * m.Bits, sh = bit & 7;
            ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(m.Data!)), bit >> 3);
            ulong w = Unsafe.ReadUnaligned<ulong>(ref p);
            Unsafe.WriteUnaligned(ref p, (w & ~(m.Mask << sh)) | (dlt << sh));
            Stats.InPlaceWrites++;
            return;
        }
        if (m.Mode == (byte)Rep.Aligned && v >= m.Base && dlt <= m.Mask)
        {
            ref byte p = ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(m.Data!));
            if (m.Bits == 16) Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, j * 2), (ushort)dlt);
            else if (m.Bits == 8) Unsafe.Add(ref p, j) = (byte)dlt;
            else Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, j * 4), (uint)dlt);
            Stats.InPlaceWrites++;
            return;
        }
        SetSlow(ci, j, v);
    }

    private void SetSlow(int ci, int j, ulong v)
    {
        var c = _c[ci];
        if (v < c.CMin || v > c.CMax) _nf[ci]++;
        c.ContentStale = true;
        if (c.Rep == Rep.Raw) { c.Raw![j] = v; return; }

        if (c.HotCount > 0)
        {
            int line = j >> LineShift;
            for (int k = 0; k < c.HotCount; k++) if (c.HotIdx![k] == line) { c.Hot![k]![j & (LineWords - 1)] = v; return; }
        }
        // запись прямо в основу, если значение помещается
        if (c.OvKey == null || !OvContains(c, j))
        {
            switch (c.Rep)
            {
                case Rep.Packed:
                    if (v >= c.Base && v - c.Base <= c.Mask)
                    {
                        int bit = j * c.Bits;
                        var sp = c.Pay.AsSpan(bit >> 3);
                        ulong w = BinaryPrimitives.ReadUInt64LittleEndian(sp);
                        int sh = bit & 7;
                        w = (w & ~(c.Mask << sh)) | ((v - c.Base) << sh);
                        BinaryPrimitives.WriteUInt64LittleEndian(sp, w);
                        Stats.InPlaceWrites++;
                        return;
                    }
                    break;
                case Rep.Aligned:
                    if (v >= c.Base && v - c.Base <= c.Mask)
                    {
                        ulong d = v - c.Base;
                        if (c.Bits == 8) c.Pay![j] = (byte)d;
                        else if (c.Bits == 16) Unsafe.WriteUnaligned(ref c.Pay![j * 2], (ushort)d);
                        else Unsafe.WriteUnaligned(ref c.Pay![j * 4], (uint)d);
                        Stats.InPlaceWrites++;
                        return;
                    }
                    break;
                case Rep.Sparse:
                {
                    int w = j >> 6; ulong b = 1UL << (j & 63);
                    bool present = (c.Bitmap![w] & b) != 0;
                    if (present && v != 0) { c.SpVals![c.Prefix![w] + BitOperations.PopCount(c.Bitmap[w] & (b - 1))] = v; Stats.InPlaceWrites++; return; }
                    if (!present && v == 0) { Stats.InPlaceWrites++; return; }
                    break;
                }
            }
        }
        // слой актуальных изменений: повторная запись обновляет существующее значение
        if (OvPut(c, ci, j, v)) { Stats.OverlayWrites++; return; }
        // слой переполнен → горячая строка в обычном виде
        if (c.HotCount < _o.MaxHotLines && _hotBytes + LineWords * 8 <= _o.HotWindowBudgetBytes)
        {
            PromoteLine(c, ci, j >> LineShift);
            c.Hot![c.HotCount - 1]![j & (LineWords - 1)] = v;
            Stats.HotLinePromotions++;
            return;
        }
        // записи по всему чанку → обычное хранение
        ToRaw(c, ci);
        c.Raw![j] = v;
        Stats.RawPromotions++;
    }

    private const ushort Tomb = 0xFFFF;

    private bool OvContains(Chunk c, int j) => OvGet(c, j, out _);

    private bool OvGet(Chunk c, int j, out ulong v)
    {
        var keys = c.OvKey!;
        int mask = keys.Length - 1, h = (int)((uint)(j * 0x9E3779B1) >> 16) & mask;
        ushort key = (ushort)(j + 1);
        for (int p = 0; p < _o.MaxProbe; p++)
        {
            int s = (h + p) & mask;
            if (keys[s] == key) { v = c.OvVal![s]; return true; }
            if (keys[s] == 0) break;
        }
        v = 0;
        return false;
    }

    private bool OvPut(Chunk c, int ci, int j, ulong v)
    {
        if (c.OvKey == null) { c.OvKey = new ushort[_o.OverlayCapacity]; c.OvVal = new ulong[_o.OverlayCapacity]; _m[ci].Mode |= SlowMode; }
        var keys = c.OvKey;
        int mask = keys.Length - 1, h = (int)((uint)(j * 0x9E3779B1) >> 16) & mask;
        ushort key = (ushort)(j + 1);
        int free = -1;
        for (int p = 0; p < _o.MaxProbe; p++)
        {
            int s = (h + p) & mask;
            if (keys[s] == key) { c.OvVal![s] = v; return true; }
            if (keys[s] == Tomb) { if (free < 0) free = s; continue; }
            if (keys[s] == 0) { if (free < 0) free = s; break; }
        }
        if (free < 0) return false;
        keys[free] = key; c.OvVal![free] = v; c.OvCount++;
        return true;
    }

    private void PromoteLine(Chunk c, int ci, int line)
    {
        c.Hot ??= new ulong[_o.MaxHotLines][];
        c.HotIdx ??= new int[_o.MaxHotLines];
        var buf = new ulong[LineWords];
        int lo = line << LineShift;
        for (int t = 0; t < LineWords; t++) buf[t] = BaseGet(c, ci, lo + t);
        // перенести записи этой строки из слоя изменений в окно и перестроить слой
        if (c.OvCount > 0)
        {
            var keys = c.OvKey!;
            for (int k = 0; k < keys.Length; k++)
            {
                if (keys[k] == 0 || keys[k] == Tomb) continue;
                int jj = keys[k] - 1;
                if (jj >> LineShift == line) { buf[jj & (LineWords - 1)] = c.OvVal![k]; keys[k] = Tomb; c.OvCount--; }
            }
        }
        c.Hot[c.HotCount] = buf; c.HotIdx[c.HotCount] = line; c.HotCount++;
        _hotBytes += LineWords * 8;
        Sync(ci);
    }

    private void ClearWindow(Chunk c)
    {
        _hotBytes -= (long)c.HotCount * LineWords * 8;
        c.Hot = null; c.HotIdx = null; c.HotCount = 0;
        c.OvKey = null; c.OvVal = null; c.OvCount = 0;
    }

    private void ToRaw(Chunk c, int ci)
    {
        var raw = new ulong[ChunkWords];
        Materialize(c, ci, raw);
        ClearWindow(c);
        if (c.Rep == Rep.Dense) DenseInvalidate(ci);
        c.Pay = null; c.Bitmap = null; c.Prefix = null; c.SpVals = null; c.Lz = null;
        c.Rep = Rep.Raw; c.Raw = raw; c.EpochsInRep = 0;
        Sync(ci);
    }

    // ───────────────────────────── кодирование ─────────────────────────────

    private static int PackedBytes(int bits) => (ChunkWords * bits + 7) / 8 + 8; // +8: безопасное 8-байтовое чтение

    private void RefreshContent(Chunk c, int ci)
    {
        if (!c.ContentStale) return;
        var d = c.Rep == Rep.Raw && !c.Dirty ? c.Raw! : MaterializeTo(c, ci);
        ulong mn = ulong.MaxValue, mx = 0; int nz = 0;
        foreach (var v in d) { if (v < mn) mn = v; if (v > mx) mx = v; if (v != 0) nz++; }
        c.CMin = mn; c.CMax = mx; c.Nz = nz; c.LzSize = -1; c.ContentStale = false;
    }

    private ulong[] MaterializeTo(Chunk c, int ci) { var t = new ulong[ChunkWords]; Materialize(c, ci, t); return t; }

    private int SizeOf(Chunk c, Rep r)
    {
        int bits = BitPack.BitsNeeded(c.CMax - c.CMin);
        int win = (c.OvKey != null ? _o.OverlayCapacity * 10 : 0) + c.HotCount * LineWords * 8;
        return r switch
        {
            Rep.Raw => ChunkWords * 8,
            Rep.Packed => bits <= 56 ? PackedBytes(Math.Max(bits, 1)) + win : -1,
            Rep.Aligned => bits <= 32 ? ChunkWords * (bits <= 8 ? 1 : bits <= 16 ? 2 : 4) + win : -1,
            Rep.Sparse => c.Nz <= ChunkWords / 4 ? 64 + 16 + c.Nz * 8 + win : -1,
            Rep.Dense => c.LzSize > 0 ? c.LzSize + win : -1,
            _ => -1,
        };
    }

    private void Encode(Chunk c, int ci, Rep r)
    {
        var d = c.Rep == Rep.Raw && !c.Dirty ? c.Raw! : MaterializeTo(c, ci);
        ClearWindow(c);
        if (c.Rep == Rep.Dense) DenseInvalidate(ci);
        c.Raw = null; c.Pay = null; c.Bitmap = null; c.Prefix = null; c.SpVals = null; c.Lz = null;
        ulong mn = ulong.MaxValue, mx = 0; int nz = 0;
        foreach (var v in d) { if (v < mn) mn = v; if (v > mx) mx = v; if (v != 0) nz++; }
        c.CMin = mn; c.CMax = mx; c.Nz = nz; c.ContentStale = false;
        int bits = BitPack.BitsNeeded(mx - mn);
        // после слияния окна значения могли перестать помещаться — тогда другой формат
        if (r == Rep.Packed && bits > 56) r = Rep.Raw;
        if (r == Rep.Aligned && bits > 32) r = bits <= 56 ? Rep.Packed : Rep.Raw;
        if (r == Rep.Sparse && nz > ChunkWords / 2) r = bits <= 56 ? Rep.Packed : Rep.Raw;
        switch (r)
        {
            case Rep.Raw: c.Raw = d == _scratch ? (ulong[])d.Clone() : d; break;
            case Rep.Packed:
            {
                bits = Math.Max(bits, 1);
                c.Bits = bits; c.Base = mn; c.Mask = BitPack.MaxValue(bits);
                c.Pay = new byte[PackedBytes(bits)];
                for (int j = 0; j < ChunkWords; j++)
                {
                    int bit = j * bits;
                    var sp = c.Pay.AsSpan(bit >> 3);
                    ulong w = BinaryPrimitives.ReadUInt64LittleEndian(sp) | ((d[j] - mn) << (bit & 7));
                    BinaryPrimitives.WriteUInt64LittleEndian(sp, w);
                }
                break;
            }
            case Rep.Aligned:
            {
                int b = bits <= 8 ? 8 : bits <= 16 ? 16 : 32;
                c.Bits = b; c.Base = mn; c.Mask = BitPack.MaxValue(b);
                c.Pay = new byte[ChunkWords * b / 8];
                for (int j = 0; j < ChunkWords; j++)
                {
                    ulong dd = d[j] - mn;
                    if (b == 8) c.Pay[j] = (byte)dd;
                    else if (b == 16) Unsafe.WriteUnaligned(ref c.Pay[j * 2], (ushort)dd);
                    else Unsafe.WriteUnaligned(ref c.Pay[j * 4], (uint)dd);
                }
                break;
            }
            case Rep.Sparse:
            {
                c.Bitmap = new ulong[ChunkWords / 64]; c.Prefix = new ushort[ChunkWords / 64];
                var vals = new List<ulong>(nz);
                for (int w = 0; w < ChunkWords / 64; w++)
                {
                    c.Prefix[w] = (ushort)vals.Count;
                    for (int b = 0; b < 64; b++) { ulong v = d[w * 64 + b]; if (v != 0) { c.Bitmap[w] |= 1UL << b; vals.Add(v); } }
                }
                c.SpVals = vals.ToArray();
                break;
            }
            case Rep.Dense:
                c.Lz = LzEncode(d); c.LzSize = c.Lz.Length + 16;
                break;
        }
        if (r != c.Rep) { Stats.Transitions++; c.EpochsInRep = 0; }
        c.Rep = r;
        Sync(ci);
    }

    private static byte[] LzEncode(ulong[] d)
    {
        var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(MemoryMarshal.AsBytes(d.AsSpan()));
        return ms.ToArray();
    }

    private static void LzDecode(byte[] src, ulong[] dst)
    {
        using var z = new DeflateStream(new MemoryStream(src), CompressionMode.Decompress);
        var b = MemoryMarshal.AsBytes(dst.AsSpan());
        int r = 0;
        while (r < b.Length) { int n = z.Read(b[r..]); if (n == 0) throw new InvalidDataException(); r += n; }
    }

    /// <summary>Принудительно задать представление всех чанков (для калибровки и экспериментов).</summary>
    public void ForceAll(Rep r)
    {
        for (int ci = 0; ci < _c.Length; ci++)
        {
            RefreshContent(_c[ci], ci);
            if (r == Rep.Dense && _c[ci].LzSize < 0) _c[ci].LzSize = 1;
            if (r == Rep.Raw || SizeOf(_c[ci], r) > 0) Encode(_c[ci], ci, r);
        }
    }

    // ───────────────────────────── политика ─────────────────────────────

    public long Footprint
    {
        get
        {
            long f = (long)_c.Length * MetaBytes;
            foreach (var c in _c)
            {
                f += c.Rep switch
                {
                    Rep.Raw => ChunkWords * 8,
                    Rep.Packed or Rep.Aligned => c.Pay!.Length,
                    Rep.Sparse => 64 + 16 + c.SpVals!.Length * 8,
                    _ => c.Lz!.Length,
                };
                if (c.OvKey != null) f += c.OvKey.Length * 10;
                f += c.HotCount * LineWords * 8;
            }
            return f + _dcKey.Length * ChunkWords * 8; // кеш распакованных чанков тоже занимает память
        }
    }

    private double Cost(Chunk c, Rep r)
    {
        // ожидаемая стоимость следующей эпохи обращений в представлении r (нс)
        double cost = c.ERand * _k.RandRead[(int)r] + c.ESeq * _k.SeqElem[(int)r];
        if (r == Rep.Raw) return cost + c.EWrite * _k.WriteFit[0];
        double fit = Math.Max(0, c.EWrite - c.ENonFit);
        double nonFit = r == Rep.Dense ? c.EWrite : c.ENonFit;
        if (r == Rep.Dense) fit = 0;
        cost += fit * _k.WriteFit[(int)r] + nonFit * _k.OverlayWrite;
        // окно не вмещает изменения → перестройка / возврат к обычному хранению каждую эпоху
        double windowCap = _o.OverlayCapacity * 0.75 + _o.MaxHotLines * LineWords;
        if (nonFit > windowCap || (nonFit > _o.OverlayCapacity * 0.75 && c.ESpread > _o.MaxHotLines))
            cost += _k.MaterializeChunk + (r == Rep.Dense ? _k.DenseEncodeChunk : _k.EncodeChunk);
        return cost;
    }

    private double TransitionCost(Chunk c, Rep to) =>
        to == c.Rep ? 0 : (_k.MaterializeChunk + (to == Rep.Dense ? _k.DenseEncodeChunk : to == Rep.Raw ? 0 : _k.EncodeChunk)) / _o.HorizonEpochs;

    /// <summary>Шаг политики (эпоха): статистика → выбор представлений при бюджете RAM → переходы и слияние окон.</summary>
    public void Maintain()
    {
        int n = _c.Length;
        var cand = new (Rep r, double cost, int size)[n][];
        for (int ci = 0; ci < n; ci++)
        {
            var c = _c[ci];
            const double a = 0.5;
            c.RandReads = _rr[ci]; c.SeqElems = _sq[ci]; c.Writes = _wr[ci]; c.NonFit = _nf[ci]; c.LinesWritten = _lw[ci];
            _rr[ci] = _sq[ci] = _wr[ci] = _nf[ci] = 0; _lw[ci] = 0;
            if (c.Writes > 0) c.ContentStale = true;
            // записи быстрого пути не проверяют диапазон: оцениваем долю «невлезающих» по факту выхода за min/max
            ulong pmin = c.CMin, pmax = c.CMax;
            c.ERand = a * c.RandReads + (1 - a) * c.ERand;
            c.ESeq = a * c.SeqElems + (1 - a) * c.ESeq;
            c.EWrite = a * c.Writes + (1 - a) * c.EWrite;
            c.ESpread = a * BitOperations.PopCount((uint)c.LinesWritten) + (1 - a) * c.ESpread;
            bool touched = c.RandReads + c.SeqElems + c.Writes > 0;
            c.IdleEpochs = touched ? 0 : c.IdleEpochs + 1;
            c.EpochsInRep++;
            bool quietWrites = c.Writes == 0;

            bool mayChange = c.EpochsInRep >= _o.MinDwellEpochs || c.ContentStale;
            if (c.ContentStale && (mayChange || quietWrites)) RefreshContent(c, ci);
            if (c.Rep == Rep.Raw && c.Writes > 0 && !c.ContentStale && (c.CMin < pmin || c.CMax > pmax)) c.NonFit += c.Writes / 2;
            c.ENonFit = a * c.NonFit + (1 - a) * c.ENonFit;
            if (!c.ContentStale && c.LzSize < 0 && (!_o.AccessAware || c.IdleEpochs >= _o.DenseAfterIdleEpochs))
                c.LzSize = LzEncode(c.Rep == Rep.Raw && !c.Dirty ? c.Raw! : MaterializeTo(c, ci)).Length + 16;

            var list = new List<(Rep, double, int)>(5);
            int cur = c.Rep == Rep.Raw ? ChunkWords * 8 : CurrentSize(c);
            list.Add((c.Rep, Cost(c, c.Rep), cur));
            if (mayChange && !c.ContentStale)
                foreach (var r in Enum.GetValues<Rep>())
                {
                    if (r == c.Rep) continue;
                    if (r == Rep.Dense && c.IdleEpochs < _o.DenseAfterIdleEpochs && _o.AccessAware) continue;
                    int sz = SizeOf(c, r);
                    if (sz < 0) continue;
                    if (r != Rep.Raw) sz -= (c.OvKey != null ? _o.OverlayCapacity * 10 : 0) + c.HotCount * LineWords * 8; // окно сливается
                    list.Add((r, Cost(c, r) + TransitionCost(c, r), sz));
                }
            cand[ci] = list.ToArray();
            // слияние горячего окна с основой после затихания записей
            if (quietWrites && c.Dirty && c.Rep != Rep.Raw) { Encode(c, ci, c.Rep); Stats.Merges++; }
        }

        // выбор: минимизировать Σ стоимость + λ·размер, подобрать λ под бюджет
        long budget = _o.BudgetBytes - (long)n * MetaBytes - _dcKey.Length * ChunkWords * 8L;
        var choice = new int[n];
        long Pick(double lambda)
        {
            long tot = 0;
            for (int ci = 0; ci < n; ci++)
            {
                var l = cand[ci]; int best = 0; double bv = double.MaxValue;
                for (int k = 0; k < l.Length; k++)
                {
                    double v = _o.AccessAware ? l[k].cost + lambda * l[k].size : l[k].size;
                    if (v < bv) { bv = v; best = k; }
                }
                choice[ci] = best; tot += l[best].size;
            }
            return tot;
        }
        long total = Pick(0);
        if (_o.AccessAware && total > budget)
        {
            double lo = 0, hi = 1e-3;
            while (Pick(hi) > budget && hi < 1e9) hi *= 4;
            for (int it = 0; it < 40; it++) { double mid = (lo + hi) / 2; if (Pick(mid) > budget) lo = mid; else hi = mid; }
            total = Pick(hi);
        }
        Stats.BudgetMet = total <= budget;
        for (int ci = 0; ci < n; ci++)
        {
            var (r, _, _) = cand[ci][choice[ci]];
            var c = _c[ci];
            if (r == c.Rep) continue;
            if (r == Rep.Raw) { ToRaw(c, ci); Stats.Transitions++; }
            else Encode(c, ci, r);
        }
        Array.Clear(Stats.ChunksByRep);
        foreach (var c in _c) Stats.ChunksByRep[(int)c.Rep]++;
    }

    private int CurrentSize(Chunk c)
    {
        int f = c.Rep switch
        {
            Rep.Packed or Rep.Aligned => c.Pay!.Length,
            Rep.Sparse => 64 + 16 + c.SpVals!.Length * 8,
            Rep.Dense => c.Lz!.Length,
            _ => ChunkWords * 8,
        };
        if (c.OvKey != null) f += c.OvKey.Length * 10;
        return f + c.HotCount * LineWords * 8;
    }
}
