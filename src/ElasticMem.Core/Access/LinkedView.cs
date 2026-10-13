using System.Runtime.CompilerServices;

namespace ElasticMem.Core.Access;

/// <summary>
/// Точное обратимое преобразование между индексами двух представлений (работаем словами по 8 байт).
/// F(A)[i] = A[Map(i)]; обратное отображение Inv(j) = i. Задаётся формулой, поэтому не занимает памяти O(n).
/// </summary>
public interface ITransform
{
    long Count { get; }                 // число слов в B
    long SourceCount { get; }           // число слов в A
    [MethodImpl(MethodImplOptions.AggressiveInlining)] long Map(long iB);   // индекс в A для B[i]
    [MethodImpl(MethodImplOptions.AggressiveInlining)] long Inv(long jA);   // индекс в B, зависящий от A[j] (-1 если нет)
}

/// <summary>Тождество: B — та же раскладка, что и A (например, копия-дубликат).</summary>
public sealed class IdentityTransform(long n) : ITransform
{
    public long Count => n;
    public long SourceCount => n;
    public long Map(long i) => i;
    public long Inv(long j) => j;
}

/// <summary>AoS → SoA: A — массив записей по f полей (слов), B — поля разложены по столбцам.</summary>
public sealed class AoSoATransform(int records, int fields) : ITransform
{
    public long Count => (long)records * fields;
    public long SourceCount => (long)records * fields;
    public long Map(long i) { long c = i / records, r = i % records; return r * fields + c; }   // B[c*n+r] = A[r*f+c]
    public long Inv(long j) { long r = j / fields, c = j % fields; return c * records + r; }
}

/// <summary>Блочная перестановка строк/записей по явной таблице порядка (меняется расположение записей).</summary>
public sealed class ReorderTransform : ITransform
{
    private readonly int[] _order, _inv; private readonly int _rec;
    public ReorderTransform(int[] order, int recordWords)
    {
        _order = order; _rec = recordWords; _inv = new int[order.Length];
        for (int i = 0; i < order.Length; i++) _inv[order[i]] = i;
    }
    public long Count => (long)_order.Length * _rec;
    public long SourceCount => Count;
    public long Map(long i) { long blk = i / _rec, off = i % _rec; return (long)_order[blk] * _rec + off; }
    public long Inv(long j) { long blk = j / _rec, off = j % _rec; return (long)_inv[blk] * _rec + off; }
}

public sealed class LinkStats
{
    public long ReadsB, WritesB, WritesA, ResidualPeak, ResidualNow, Rematerializations;
    public bool Materialized;
}

/// <summary>
/// Связанное представление B = F(A) ⊕ R. A хранится конкретно, B — виртуально поверх A и разреженного остатка R.
/// Приложение видит два независимых диапазона: запись в B меняет только R; запись в A компенсируется в R, чтобы
/// логические байты B не менялись. Когда остаток переполняет бюджет, B материализуется в обычный буфер (безопасный откат).
/// </summary>
public sealed class LinkedView
{
    private readonly ulong[] _a;           // исходный буфер A (слова)
    private readonly ITransform _f;
    private ulong[]? _bFull;               // материализованный B (после отката)
    private ushort[]? _rKey;               // открытая адресация: индекс B (+1), 0 — пусто, Tomb — удалён
    private long[]? _rIdx;                  // полный индекс B (ushort мало — используем long в параллельном массиве)
    private ulong[]? _rVal;                 // остаток (XOR)
    private int _rCount, _rCap, _probe = 8;
    private readonly long _residualBudgetBytes; // откат, когда остаток+индекс дороже этой доли |B|
    private const ulong Empty = 0;

    public LinkStats Stats { get; } = new();
    public long CountA => _a.LongLength;
    public long CountB => _f.Count;
    public ITransform Transform => _f;

    public LinkedView(ulong[] a, ITransform f, double residualBudgetFraction = 0.6)
    {
        if (f.SourceCount != a.LongLength) throw new ArgumentException("F.SourceCount ≠ |A|");
        _a = a; _f = f;
        _residualBudgetBytes = (long)(f.Count * 8 * residualBudgetFraction);
        _rCap = 16; AllocR();
    }

    private void AllocR() { _rIdx = new long[_rCap]; _rVal = new ulong[_rCap]; }

    // ───────── A: приложение видит обычный буфер ─────────
    public ulong GetA(long j) => _a[j];

    public void SetA(long j, ulong v)
    {
        Stats.WritesA++;
        if (_a[j] == v) return;
        if (_bFull == null)
        {
            // компенсация: R_new[i] = R_old[i] ⊕ F(A_old)[i] ⊕ F(A_new)[i]; для перестановки затронут один i
            long i = _f.Inv(j);
            if (i >= 0)
            {
                ulong delta = _a[j] ^ v;      // F(A)[i] меняется на эту же XOR-дельту
                XorResidual(i, delta);
                _a[j] = v;
                CheckBudget();
                return;
            }
        }
        _a[j] = v;
    }

    // ───────── B: виртуальный буфер поверх A ⊕ R ─────────
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetB(long i)
    {
        Stats.ReadsB++;
        if (_bFull != null) return _bFull[i];
        ulong baseVal = _a[_f.Map(i)];
        return RTryGet(i, out ulong r) ? baseVal ^ r : baseVal;
    }

    public void SetB(long i, ulong v)
    {
        Stats.WritesB++;
        if (_bFull != null) { _bFull[i] = v; return; }
        ulong baseVal = _a[_f.Map(i)];
        SetResidual(i, v ^ baseVal);       // v == base → остаток 0 (удаляется)
        CheckBudget();
    }

    /// <summary>Логический размер B в байтах, который видит приложение (не зависит от сжатия).</summary>
    public long LogicalBytesB => _f.Count * 8;

    /// <summary>Вся реально занятая память связки (без A, которым владеет приложение): остаток + индекс, либо полный B.</summary>
    public long OverheadBytesB => _bFull != null ? _bFull.LongLength * 8 : (long)_rCap * (8 + 8 + 2) + 64;

    // ───────── остаток R: открытая адресация ─────────
    private const ushort KUsed = 1;
    private ushort[]? _slotState; // 0 пусто, 1 занято, 2 удалён

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(long i, int cap) => (int)((ulong)i * 0x9E3779B97F4A7C15UL >> 40) & (cap - 1);

    private bool RTryGet(long i, out ulong val)
    {
        val = 0;
        if (_rCount == 0) return false;
        var st = _slotState!; int cap = _rCap, h = Hash(i, cap);
        for (int p = 0; p < cap; p++)
        {
            int s = (h + p) & (cap - 1);
            byte state = (byte)st[s];
            if (state == 0) return false;
            if (state == 1 && _rIdx![s] == i) { val = _rVal![s]; return true; }
        }
        return false;
    }

    private void XorResidual(long i, ulong delta)
    {
        RTryGet(i, out ulong cur);
        SetResidual(i, cur ^ delta);
    }

    private void SetResidual(long i, ulong residual)
    {
        var st = _slotState ??= new ushort[_rCap];
        int cap = _rCap, h = Hash(i, cap), firstTomb = -1;
        for (int p = 0; p < cap; p++)
        {
            int s = (h + p) & (cap - 1);
            byte state = (byte)st[s];
            if (state == 1 && _rIdx![s] == i)
            {
                if (residual == 0) { st[s] = 2; _rCount--; }
                else _rVal![s] = residual;
                Track(); return;
            }
            if (state == 2) { if (firstTomb < 0) firstTomb = s; continue; }
            if (state == 0)
            {
                if (residual == 0) { Track(); return; }
                int slot = firstTomb >= 0 ? firstTomb : s;
                st[slot] = 1; _rIdx![slot] = i; _rVal![slot] = residual; _rCount++;
                if (_rCount * 4 >= cap * 3) Grow();
                Track(); return;
            }
        }
        if (residual != 0) { Grow(); SetResidual(i, residual); }
    }

    private void Track()
    {
        Stats.ResidualNow = _rCount;
        if (_rCount > Stats.ResidualPeak) Stats.ResidualPeak = _rCount;
    }

    private void Grow()
    {
        int old = _rCap; var oldIdx = _rIdx!; var oldVal = _rVal!; var oldSt = _slotState!;
        _rCap *= 2; AllocR(); _slotState = new ushort[_rCap]; _rCount = 0;
        for (int s = 0; s < old; s++) if ((byte)oldSt[s] == 1) SetResidual(oldIdx[s], oldVal[s]);
    }

    private void CheckBudget() { if (_bFull == null && OverheadBytesB > _residualBudgetBytes) Materialize(); }

    private void Materialize()
    {
        var b = new ulong[_f.Count];
        for (long i = 0; i < b.LongLength; i++) b[i] = GetBRaw(i);
        _bFull = b; _rIdx = null; _rVal = null; _slotState = null; _rCount = 0;
        Stats.Rematerializations++; Stats.Materialized = true;
    }

    private ulong GetBRaw(long i)
    {
        ulong baseVal = _a[_f.Map(i)];
        return RTryGet(i, out ulong r) ? baseVal ^ r : baseVal;
    }

    /// <summary>Приложение привело B в соответствие с новым A (остатки для диапазона исчезают).</summary>
    public void ResyncB(long start, long count)
    {
        if (_bFull != null) { for (long i = start; i < start + count; i++) _bFull[i] = _a[_f.Map(i)]; return; }
        for (long i = start; i < start + count; i++) SetResidual(i, 0);
    }
}
