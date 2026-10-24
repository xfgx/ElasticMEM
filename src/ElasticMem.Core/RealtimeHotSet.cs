namespace ElasticMem.Core;

/// <summary>
/// Ядро «сжатия в реальном времени»: по какому адресному региону процесса принимать решение
/// «держать горячим (prefetch)» или «отдать под сжатие». Чистая логика без вызовов Windows, поэтому тестируется.
///
/// Сигнал доступа получаем в стиле DAMON: после вытеснения рабочего набора смотрим, какие регионы процесс
/// сам вернул за короткое окно — это и есть «горячие сейчас». По ним ведём затухающую оценку (EWMA),
/// плюс матрицу совместного доступа для опережающей подгрузки групп. Агрессивность подстраивается по
/// обратной связи: отклик окна и доля возвратов (refault).
/// Регион — это 64 КиБ адресного пространства (ключ = адрес >> 16).
/// </summary>
public sealed class RealtimeHotSet
{
    public const int RegionBits = 16;
    public const long RegionBytes = 1 << RegionBits;

    readonly Dictionary<ulong, double> _score = new();
    readonly Dictionary<ulong, Dictionary<ulong, int>> _coaccess = new();
    readonly Dictionary<ulong, int> _count = new(); // в скольких интервалах регион был тронут
    readonly double _decay;
    double _hotThreshold; // регион горячий, если score >= порога
    public double HotThreshold => _hotThreshold;
    public double Decay => _decay;
    public int TrackedRegions => _score.Count;

    public RealtimeHotSet(double decay = 0.5, double hotThreshold = 0.35)
    {
        if (decay is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(decay));
        if (hotThreshold is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(hotThreshold));
        _decay = decay; _hotThreshold = hotThreshold;
    }

    public static ulong RegionOf(ulong address) => address >> RegionBits;

    /// <summary>Учесть один интервал наблюдения: какие регионы процесс затронул (вернул после вытеснения).</summary>
    public void Observe(IReadOnlyCollection<ulong> accessedRegions)
    {
        ArgumentNullException.ThrowIfNull(accessedRegions);
        var hit = accessedRegions as HashSet<ulong> ?? new HashSet<ulong>(accessedRegions);
        // EWMA по всем известным регионам: затухание + 1, если тронут в этом интервале.
        foreach (var key in _score.Keys.ToList())
            _score[key] = _score[key] * _decay + (hit.Contains(key) ? (1 - _decay) : 0);
        foreach (var key in hit)
        {
            if (!_score.ContainsKey(key)) _score[key] = 1 - _decay;
            _count[key] = _count.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        // Совместный доступ. Раньше считались ВСЕ пары: при 4096 регионах это 16,7 млн записей за интервал
        // на процесс — гигабайты памяти и нагрузка на CPU. Теперь берём не больше MaxCoaccessSample регионов
        // (детерминированная выборка), т. е. не больше ~2000 пар, и ограничиваем число соседей у региона.
        if (hit.Count > 1)
        {
            var arr = hit.Count <= MaxCoaccessSample ? hit.ToArray() : Sample(hit, MaxCoaccessSample, ++_interval);
            for (int i = 0; i < arr.Length; i++)
                for (int j = i + 1; j < arr.Length; j++)
                { Bump(arr[i], arr[j]); Bump(arr[j], arr[i]); }
        }
        // Не растём без предела: забываем давно холодные регионы.
        if (_score.Count > 200_000)
            foreach (var key in _score.Where(kv => kv.Value < 0.01).Select(kv => kv.Key).ToList())
            { _score.Remove(key); _coaccess.Remove(key); _count.Remove(key); }
    }

    public const int MaxCoaccessSample = 64, MaxNeighbors = 32;
    long _interval;
    public long CoaccessEntries => _coaccess.Values.Sum(m => (long)m.Count);

    static ulong[] Sample(HashSet<ulong> set, int k, long salt)
    {
        // Детерминированно для теста, но меняется от интервала к интервалу: сортировка по хешу (регион, номер интервала).
        return set.OrderBy(r => unchecked((r * 0x9E3779B97F4A7C15UL) ^ (ulong)salt * 0xBF58476D1CE4E5B9UL)).Take(k).ToArray();
    }

    void Bump(ulong a, ulong b)
    {
        if (!_coaccess.TryGetValue(a, out var m)) { m = new(); _coaccess[a] = m; }
        if (m.TryGetValue(b, out int c)) { m[b] = Math.Min(c + 1, 1_000_000); return; }
        if (m.Count >= MaxNeighbors)
        {
            // Освобождаем место: выкидываем самого редкого соседа, если новый не хуже.
            var weakest = m.MinBy(kv => kv.Value);
            if (weakest.Value > 1) return;
            m.Remove(weakest.Key);
        }
        m[b] = 1;
    }

    /// <summary>
    /// Нужно ли сейчас трогать процесс. Если рабочий набор почти не вырос с прошлого раза, сжимать нечего,
    /// а вытеснение заставит его распаковывать своё же горячее (лишний CPU). settledBytes — набор после прошлого цикла.
    /// </summary>
    public static bool NeedsCompaction(long wsNow, long settledBytes) =>
        settledBytes <= 0 || wsNow > settledBytes + Math.Max(8L << 20, settledBytes / 4);

    /// <summary>Регионы, которые стоит держать горячими: сами горячие + их частые «соседи» по группе.</summary>
    public HashSet<ulong> HotRegions(int maxRegions = 65536)
    {
        var hot = new HashSet<ulong>();
        foreach (var kv in _score)
            if (kv.Value >= _hotThreshold) hot.Add(kv.Key);
        // Группы: к каждому горячему региону добавляем соседей, с которыми он встречался чаще половины его появлений.
        foreach (var key in hot.ToList())
            if (_coaccess.TryGetValue(key, out var m) && _count.TryGetValue(key, out int kc) && kc > 0)
            {
                foreach (var nb in m)
                    // Сосед нужен вместе с горячим не реже, чем в половине его появлений, и сам не ледяной.
                    if (nb.Value * 2 >= kc && _score.TryGetValue(nb.Key, out double s) && s >= _hotThreshold * 0.5)
                        hot.Add(nb.Key);
            }
        if (hot.Count <= maxRegions) return hot;
        // Бюджет превышен — оставляем самые горячие.
        return hot.OrderByDescending(k => _score.TryGetValue(k, out double s) ? s : 0).Take(maxRegions).ToHashSet();
    }

    /// <summary>
    /// Обратная связь после цикла. responseMs — отклик окна; refaultRate — доля вернувшихся страниц (0..1).
    /// Приложению «тяжело» → снижаем порог (держим горячим больше). Всё спокойно → поднимаем (сжимаем больше).
    /// </summary>
    public void Feedback(double responseMs, double refaultRate, double responseBudgetMs = 50, double refaultBudget = 0.5)
    {
        bool stressed = responseMs > responseBudgetMs || refaultRate > refaultBudget;
        if (stressed) _hotThreshold = Math.Max(0.1, _hotThreshold - 0.05);
        else _hotThreshold = Math.Min(0.8, _hotThreshold + 0.02);
    }

    public double ScoreOf(ulong region) => _score.TryGetValue(region, out double s) ? s : 0;
}
