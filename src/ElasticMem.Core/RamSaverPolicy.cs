namespace ElasticMem.Core;

/// <summary>
/// Решение «сжимать ли процесс сейчас» для вкладки «Освободить память». Чистая функция без вызовов Windows API,
/// поэтому её можно тестировать. Ручной клик сжимает всё подходящее. Фоновый повтор сжимает только процессы,
/// которые весь интервал простаивали, и пропускает «горячие» — те, что после прошлого сжатия снова набрали
/// большую часть памяти. Иначе повтор превращается в «карусель»: в v0.7 за 10 минут было вытеснено 36 ГБ
/// при рабочем наборе ~2 ГБ, потому что одни и те же страницы гонялись туда-обратно.
/// </summary>
public static class RamSaverPolicy
{
    public enum Decision { Trim, RecordBaseline, SkipActive, SkipHot }

    public sealed class Memo { public long Cpu; public long WsBefore; }

    /// <summary>Порог простоя: рост процессорного времени за интервал меньше 50 мс (единицы 100 нс).</summary>
    public const long IdleCpuBudget100ns = 50 * 10_000;

    /// <summary>Процесс «горячий», если снова набрал больше этой доли прежнего (до сжатия) рабочего набора.</summary>
    public const double HotRefillFraction = 0.6;

    /// <param name="idleOnly">true — фоновый повтор; false — ручной клик пользователя.</param>
    /// <param name="memo">Состояние с прошлого прохода или null, если процесс видим впервые.</param>
    /// <param name="cpu">Суммарное процессорное время (kernel+user, 100 нс) или -1, если не удалось прочитать.</param>
    /// <param name="wsBefore">Текущий рабочий набор в байтах, до сжатия.</param>
    public static Decision Decide(bool idleOnly, Memo? memo, long cpu, long wsBefore)
    {
        if (!idleOnly) return Decision.Trim;
        // Без базы с прошлого прохода (или без счётчика CPU) нельзя сказать, простаивал ли процесс.
        if (memo == null || cpu < 0) return Decision.RecordBaseline;
        if (cpu - memo.Cpu > IdleCpuBudget100ns) return Decision.SkipActive;
        if (memo.WsBefore > 0 && wsBefore > memo.WsBefore * HotRefillFraction) return Decision.SkipHot;
        return Decision.Trim;
    }
}
