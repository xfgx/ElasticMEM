using ElasticMem.Core;
using static ElasticMem.Core.RamSaverPolicy;
static class RamSaverPolicyTests
{
    public static int Run()
    {
        int checks = 0, fails = 0; void C(bool ok, string m) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL RAM SAVER: " + m); } }
        const long Mb = 1L << 20, Ms = 10_000; // 1 мс = 10 000 единиц по 100 нс

        // Ручной клик: сжимаем всё подходящее, даже без истории и даже активное.
        C(Decide(false, null, -1, 500 * Mb) == Decision.Trim, "manual click trims without history");
        C(Decide(false, new Memo { Cpu = 0, WsBefore = 100 * Mb }, 10_000 * Ms, 500 * Mb) == Decision.Trim, "manual click trims even busy/hot process");

        // Повтор: без базы не трогаем, только запоминаем.
        C(Decide(true, null, 1000 * Ms, 300 * Mb) == Decision.RecordBaseline, "first sighting only records baseline");
        C(Decide(true, new Memo { Cpu = 0, WsBefore = 100 * Mb }, -1, 10 * Mb) == Decision.RecordBaseline, "unreadable CPU never trims");

        // Повтор: простоял весь интервал и памяти мало вернул -> сжимаем.
        var idle = new Memo { Cpu = 1000 * Ms, WsBefore = 400 * Mb };
        C(Decide(true, idle, 1000 * Ms + 10 * Ms, 40 * Mb) == Decision.Trim, "idle cold process is trimmed");
        C(Decide(true, idle, 1000 * Ms + 50 * Ms, 40 * Mb) == Decision.Trim, "exactly at 50ms budget still idle");

        // Повтор: активный (CPU вырос больше 50 мс) -> не трогаем.
        C(Decide(true, idle, 1000 * Ms + 51 * Ms, 40 * Mb) == Decision.SkipActive, "active process skipped");
        C(Decide(true, idle, 1000 * Ms + 5000 * Ms, 40 * Mb) == Decision.SkipActive, "busy process skipped");

        // Повтор: «горячий» — снова набрал >60% прежнего набора -> не гоняем страницы туда-обратно.
        C(Decide(true, idle, 1000 * Ms, 241 * Mb) == Decision.SkipHot, "refilled >60% is hot");
        C(Decide(true, idle, 1000 * Ms, 240 * Mb) == Decision.Trim, "exactly 60% is not hot");
        C(Decide(true, idle, 1000 * Ms, 400 * Mb) == Decision.SkipHot, "fully refilled is hot");

        // Активность важнее «горячести»: занятый процесс помечается как активный.
        C(Decide(true, idle, 1000 * Ms + 100 * Ms, 400 * Mb) == Decision.SkipActive, "active wins over hot");

        // Нет данных о прежнем наборе -> «горячим» не считаем.
        C(Decide(true, new Memo { Cpu = 0, WsBefore = 0 }, 0, 900 * Mb) == Decision.Trim, "unknown previous size not treated as hot");

        // Карусель: процесс, который каждый раз возвращает память, после первого сжатия больше не трогается.
        // Имитация 10 повторов: процесс простаивает по CPU, но к каждому проходу снова отрастает до 90%.
        var m = new Memo { Cpu = 0, WsBefore = 1000 * Mb }; int trims = 0;
        for (int i = 0; i < 10; i++)
        {
            var d = Decide(true, m, 0, 900 * Mb);
            if (d == Decision.Trim) { trims++; m.WsBefore = 900 * Mb; }
            else if (d == Decision.SkipHot) { m.WsBefore = 900 * Mb; }
        }
        C(trims == 0, "refilling process is never re-trimmed in a loop (no carousel)");

        Console.WriteLine($"RAM SAVER POLICY {checks} checks; pure decision logic, NOT native EmptyWorkingSet");
        return fails;
    }
}
