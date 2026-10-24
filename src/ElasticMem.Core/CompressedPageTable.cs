using ElasticMem.Core.Backend.Vmm;
namespace ElasticMem.Core;

// Таблица страниц для прозрачно-сжатой кучи. Чистая логика без вызовов Windows, поэтому тестируется.
// Состояние каждой 4-КиБ страницы: Zero (не трогали / нули), Resident (живые байты у вызывающего),
// Compressed (сжатая копия в сторонном хранилище). Сам обработчик отказов страниц (VEH) и VirtualProtect —
// в оконной части ElasticHeap; здесь только «что хранить и кого вытеснять».
public sealed class CompressedPageTable : IDisposable
{
    public enum PageState : byte { Zero, Resident, Compressed, Pinned }
    public const int PageBytes = 4096;

    readonly UserPageStore _store;
    readonly PageState[] _state;
    readonly ulong[] _generation;
    readonly long[] _stamp;      // «часы доступа» для LRU: когда страница в последний раз становилась резидентной
    long _clock;
    public int PageCount { get; }
    public int Resident { get; private set; }
    public int Compressed { get; private set; }
    public int Pinned { get; private set; }

    public CompressedPageTable(IPageCodec codec, int pages, long quotaBytes)
    {
        if (pages is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(pages));
        _store = new UserPageStore(codec, pages, quotaBytes);
        _state = new PageState[pages]; _generation = new ulong[pages]; _stamp = new long[pages];
        PageCount = pages;
    }

    public PageState State(int page) => _state[Check(page)];
    int Check(int page) { if ((uint)page >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(page)); return page; }

    /// <summary>Отказ доступа к странице: заполнить dst её текущим содержимым и пометить резидентной.
    /// Возвращает true, если данные распаковывались (для учёта задержки).</summary>
    public bool OnFault(int page, Span<byte> dst)
    {
        Check(page); if (dst.Length != PageBytes) throw new ArgumentException("4096-byte page required");
        bool decompressed = false;
        switch (_state[page])
        {
            case PageState.Compressed:
                byte[] data = _store.Get((uint)page, _generation[page]);
                data.CopyTo(dst); Array.Clear(data);
                _store.Delete((uint)page, _generation[page]); _generation[page] = 0;
                Compressed--; decompressed = true;
                break;
            case PageState.Zero:
                dst.Clear();
                break;
            case PageState.Resident:
            case PageState.Pinned:
                return false; // уже резидентна, ничего делать не нужно
        }
        _state[page] = PageState.Resident; _stamp[page] = ++_clock; Resident++;
        return decompressed;
    }

    /// <summary>Вытеснить резидентную страницу: прочитать байты из src, сжать и сохранить (или пометить Zero).
    /// Несжимаемую страницу закрепляет (Pinned) и оставляет резидентной. Возвращает сохранённые байты полезной нагрузки.</summary>
    public int Evict(int page, ReadOnlySpan<byte> src)
    {
        Check(page); if (src.Length != PageBytes) throw new ArgumentException("4096-byte page required");
        if (_state[page] != PageState.Resident) return 0;
        bool allZero = true;
        foreach (byte b in src) if (b != 0) { allZero = false; break; }
        if (allZero) { _state[page] = PageState.Zero; Resident--; return 0; }
        try
        {
            var r = _store.Put((uint)page, src.ToArray());
            _generation[page] = r.Generation; _state[page] = PageState.Compressed; Resident--; Compressed++;
            return (int)r.PayloadBytes;
        }
        catch (PageNotBeneficialException) { _state[page] = PageState.Pinned; Resident--; Pinned++; return 0; }
        catch (IOException) { _state[page] = PageState.Pinned; Resident--; Pinned++; return 0; } // квота: держим резидентной
    }

    /// <summary>Самые холодные резидентные страницы (по времени последнего доступа), не больше count.</summary>
    public int[] ChooseCold(int count)
    {
        if (count <= 0) return Array.Empty<int>();
        var resident = new List<int>();
        for (int i = 0; i < PageCount; i++) if (_state[i] == PageState.Resident) resident.Add(i);
        resident.Sort((a, b) => _stamp[a].CompareTo(_stamp[b]));
        return resident.Count <= count ? resident.ToArray() : resident.GetRange(0, count).ToArray();
    }

    public void Touch(int page) { if (_state[Check(page)] is PageState.Resident) _stamp[page] = ++_clock; }

    public UserStoreStats StoreStats() => _store.Stats();
    public long LogicalBytes => (long)PageCount * PageBytes;
    public long ResidentBytes => (long)Resident * PageBytes + (long)Pinned * PageBytes;
    public void Dispose() => _store.Dispose();
}
