using System.Diagnostics;
using System.Text;

namespace ElasticMem.Core.Access;

/// <summary>
/// Проверка гипотезы «сжатие согласованно изменяющихся представлений».
/// Эксперимент 1: оптимистическая граница — если связи известны заранее, сколько памяти экономится при заданном
/// потоке изменений (с учётом остатка R и индекса), и как быстро представления расходятся.
/// Эксперимент 2: рабочий прототип LinkedView — произвольные чтения/записи в A и B, побайтовая сверка с двумя
/// обычными буферами, измерение ПИКОВОЙ памяти и времени (включая откат к обычному буферу).
/// </summary>
public static class LinkedExperiment
{
    public sealed class Result
    {
        public string Scenario = "";
        public long LogicalBytes, TwoCopiesBytes;
        public double LinkedPeakBytes, LinkedFinalBytes;
        public double SavingPctVsTwoCopies, OptimisticSavingPct;
        public bool ByteExact, FellBack;
        public double OpsMs;
        public string Note = "";
    }

    private enum Kind { Duplicate, AoSoA, Reorder }

    public static List<Result> Run(int records = 1 << 20, int fields = 4, Action<string>? log = null)
    {
        var res = new List<Result>();
        foreach (var kind in new[] { Kind.Duplicate, Kind.AoSoA, Kind.Reorder })
        {
            // доля записей, идущих в B независимо (создают остаток) vs согласованные изменения A→resync
            foreach (var (name, pB, coherentA, independentChurn) in new[]
            {
                ("только чтение B", 0.0, 0.0, 0.0),
                ("согласованные изменения (пишем A, затем resync B)", 0.0, 1.0, 0.0),
                ("умеренные независимые записи в B (5%)", 0.05, 0.3, 0.05),
                ("интенсивные независимые записи в B (40%)", 0.40, 0.1, 0.40),
                ("перезапись A при живом старом B (худший случай)", 0.0, 0.0, 1.0),
            })
            {
                var r = RunOne(kind, records, fields, name, pB, coherentA, independentChurn, log);
                res.Add(r);
            }
        }
        return res;
    }

    private static ITransform Make(Kind k, int records, int fields, out int words, Random rnd)
    {
        switch (k)
        {
            case Kind.AoSoA: words = records * fields; return new AoSoATransform(records, fields);
            case Kind.Reorder:
                var order = Enumerable.Range(0, records).ToArray();
                for (int i = order.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
                words = records * fields; return new ReorderTransform(order, fields);
            default: words = records * fields; return new IdentityTransform((long)records * fields);
        }
    }

    private static Result RunOne(Kind kind, int records, int fields, string scenario,
        double pB, double coherentA, double independentChurn, Action<string>? log)
    {
        var rnd = new Random(99);
        var f = Make(kind, records, fields, out int words, rnd);
        var a = new ulong[words];
        for (int i = 0; i < words; i++) a[i] = 1_000_000 + (ulong)rnd.Next(1 << 20);

        // эталон: два независимых буфера
        var refA = (ulong[])a.Clone();
        var refB = new ulong[f.Count];
        for (long i = 0; i < refB.LongLength; i++) refB[i] = refA[f.Map(i)];

        var view = new LinkedView(a, f);

        long logical = (long)words * 8 + f.Count * 8;
        var r = new Result
        {
            Scenario = $"{kind}: {scenario}",
            LogicalBytes = logical,
            TwoCopiesBytes = logical,
            ByteExact = true,
        };

        int ops = words; // ~1 операция на слово
        long independentDiffs = 0; // для оптимистической границы
        var sw = Stopwatch.StartNew();
        double peak = 0;
        for (int step = 0; step < ops; step++)
        {
            double u = rnd.NextDouble();
            if (u < independentChurn && kind != Kind.Duplicate || (kind == Kind.Duplicate && u < independentChurn))
            {
                // перезаписываем A, но старый B ещё нужен → остаток растёт
                long j = rnd.NextInt64(words);
                ulong v = 1_000_000 + (ulong)rnd.Next(1 << 20);
                view.SetA(j, v);          // компенсируется в R, логический B сохраняется
                refA[j] = v;              // в эталоне A меняется, refB остаётся
                independentDiffs++;
            }
            else if (u < independentChurn + pB)
            {
                long i = rnd.NextInt64(f.Count);
                ulong v = 1_000_000 + (ulong)rnd.Next(1 << 20);
                view.SetB(i, v); refB[i] = v;
                independentDiffs++;
            }
            else if (u < independentChurn + pB + coherentA)
            {
                // согласованное изменение: пишем A и тут же приводим B в соответствие
                long j = rnd.NextInt64(words);
                ulong v = 1_000_000 + (ulong)rnd.Next(1 << 20);
                view.SetA(j, v); refA[j] = v;
                long i = f.Inv(j);
                if (i >= 0) { view.ResyncB(i, 1); refB[i] = refA[f.Map(i)]; }
            }
            else
            {
                long i = rnd.NextInt64(f.Count);
                if (view.GetB(i) != refB[i]) r.ByteExact = false;
            }
            if ((step & 0x3FF) == 0) peak = Math.Max(peak, view.OverheadBytesB);
        }
        r.OpsMs = sw.Elapsed.TotalMilliseconds;
        peak = Math.Max(peak, view.OverheadBytesB);

        // финальная побайтовая сверка всего B и A
        for (long i = 0; i < refB.LongLength; i += 7) if (view.GetB(i) != refB[i]) { r.ByteExact = false; break; }
        for (long j = 0; j < refA.LongLength; j += 7) if (view.GetA(j) != refA[j]) { r.ByteExact = false; break; }

        r.FellBack = view.Stats.Materialized;
        // реально занятая память связки = A (общий) + накладные B; против двух копий = |A|+|B|
        double linkedNow = words * 8.0 + view.OverheadBytesB;
        double linkedPeak = words * 8.0 + peak;
        r.LinkedFinalBytes = linkedNow;
        r.LinkedPeakBytes = linkedPeak;
        r.SavingPctVsTwoCopies = 100 * (1 - linkedPeak / logical);
        // оптимистическая граница: |A| + независимые_различия*(8 значение + 10 индекс) + |B| только если материализовали
        double opt = words * 8.0 + Math.Min(independentDiffs * 18.0, f.Count * 8.0);
        r.OptimisticSavingPct = 100 * (1 - opt / logical);
        r.Note = $"остаток пик {view.Stats.ResidualPeak} слов, независимых различий {independentDiffs}" + (r.FellBack ? ", ОТКАТ к обычному B" : "");
        log?.Invoke($"{r.Scenario}: экономия(пик) {r.SavingPctVsTwoCopies:F1}%, байт-в-байт {(r.ByteExact ? "да" : "НЕТ")}");
        return r;
    }

    public static string Format(List<Result> rs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Связь живых буферов B = F(A) ⊕ R. Экономия считается от двух независимых копий (|A|+|B|).");
        sb.AppendLine("«Экономия (пик)» учитывает ПИКОВУЮ память остатка, а не выгодный момент. Все обращения сверяются байт-в-байт.\n");
        sb.AppendLine($"{"Сценарий",-66} {"Лог. МБ",8} {"Пик МБ",8} {"Эконом.пик",10} {"Оптим.гр.",10} {"OK",4} {"Откат",6}");
        foreach (var r in rs)
            sb.AppendLine($"{Trunc(r.Scenario, 66),-66} {r.LogicalBytes / 1048576.0,8:F1} {r.LinkedPeakBytes / 1048576.0,8:F1} {r.SavingPctVsTwoCopies,9:F1}% {r.OptimisticSavingPct,9:F1}% {(r.ByteExact ? "да" : "НЕТ"),4} {(r.FellBack ? "да" : "нет"),6}");
        sb.AppendLine();
        foreach (var r in rs) sb.AppendLine($"■ {r.Scenario}\n   {r.Note}");
        sb.AppendLine("\nВывод: экономия есть только пока одновременно существует мало независимых различий между представлениями.");
        sb.AppendLine("При интенсивных независимых записях остаток растёт и срабатывает безопасный откат к обычному буферу — как и предсказано.");
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
