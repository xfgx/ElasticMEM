using System.Globalization;
using System.Text.Json;

namespace ElasticMem.Core.Research;
public record GapCurve(double MeanBudgetUs, double BaselineMeanBytes, double ModelMeanSavedBytes, double ModelSavedFraction, long Recoveries, double ModeledMeanStallUs);
public static class GapModel
{
    public static object Run(string trace, string metadata, double evictionUs, double recoveryUs, double maxFaultUs, double[] budgets, CancellationToken ct = default)
    {
        if (!(evictionUs > 0 && recoveryUs > 0 && maxFaultUs >= 0) || !double.IsFinite(evictionUs + recoveryUs + maxFaultUs) || budgets.Length == 0 || budgets.Any(v => !double.IsFinite(v) || v < 0)) throw new ArgumentException("Invalid costs/budgets");
        using var doc = JsonDocument.Parse(File.ReadAllText(metadata)); var m = doc.RootElement;
        string kind = m.GetProperty("trace_kind").GetString()!;
        if (m.GetProperty("schema").GetString() != "elasticmem.events.v1" || kind is not ("exact" or "synthetic")) throw new InvalidDataException("Sampled data are not an exact access trace");
        foreach (var key in new[] { "source", "scope", "clock", "access_definition", "process_identity" }) if (string.IsNullOrWhiteSpace(m.GetProperty(key).GetString())) throw new InvalidDataException("Missing provenance: " + key);
        if (m.GetProperty("event_loss_count").GetInt32() != 0) throw new InvalidDataException("Lost events");
        if (m.GetProperty("limitations").ValueKind != JsonValueKind.Array || m.GetProperty("limitations").EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new InvalidDataException("Invalid limitations");
        int ps = m.GetProperty("page_size").GetInt32(); long start = m.GetProperty("start_ns").GetInt64(), end = m.GetProperty("end_ns").GetInt64();
        if (ps <= 0 || (ps & (ps - 1)) != 0 || start < 0 || end <= start) throw new InvalidDataException("Invalid window/page size");
        var live = new Dictionary<(int, ulong), (long Map, long Last, bool Accessed)>(); var stats = new Dictionary<int, (double PageTime, long Accesses)>();
        var gaps = new List<(int Pid, double Score)>(); long previous = start, events = 0;
        using var reader = File.OpenText(trace); if (reader.ReadLine() != "timestamp_ns,pid,page,op") throw new InvalidDataException("Unexpected header");
        string? line; while ((line = reader.ReadLine()) != null)
        {
            events++; if(events % 4096 == 0) ct.ThrowIfCancellationRequested(); var p = line.Split(','); if (p.Length != 4) throw new InvalidDataException("Malformed row " + events);
            long t = long.Parse(p[0], CultureInfo.InvariantCulture); int pid = int.Parse(p[1], CultureInfo.InvariantCulture);
            ulong page = p[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(p[2][2..], NumberStyles.HexNumber) : ulong.Parse(p[2], CultureInfo.InvariantCulture);
            if (t < previous || t < start || t > end || pid <= 0 || page % (uint)ps != 0) throw new InvalidDataException("Invalid event " + events); previous = t;
            var key = (pid, page); stats.TryGetValue(pid, out var s);
            switch (p[3])
            {
                case "map": if (!live.TryAdd(key, (t,t,false))) throw new InvalidDataException("Duplicate map"); break;
                case "read": case "write":
                    if (!live.TryGetValue(key, out var state)) throw new InvalidDataException("Access without map");
                    // Interior gaps only. Objective is defined score, NOT demand-paging upper bound.
                    if (state.Accessed) { double score = Math.Max(0, t - state.Last - (evictionUs + recoveryUs) * 1000); if (score > 0) gaps.Add((pid, score)); }
                    live[key] = (state.Map,t,true); s.Accesses++; break;
                case "unmap": if (!live.Remove(key, out var old)) throw new InvalidDataException("Unmap without map"); s.PageTime += t - old.Map; break;
                default: throw new InvalidDataException("Unknown operation");
            }
            stats[pid] = s;
        }
        foreach (var item in live) { var s = stats[item.Key.Item1]; s.PageTime += end - item.Value.Map; stats[item.Key.Item1] = s; }
        double pageTime = stats.Values.Sum(s => s.PageTime); long accesses = stats.Values.Sum(s => s.Accesses);
        if (events == 0 || pageTime <= 0) throw new InvalidDataException("Empty trace");
        gaps = gaps.OrderByDescending(g => g.Score).ThenBy(g => g.Pid).ToList();
        var curves = new List<object>();
        foreach (double budget in budgets.Distinct().Order())
        {
            int count = recoveryUs <= maxFaultUs ? (int)Math.Min(gaps.Count, Math.Floor(budget * accesses / recoveryUs + 1e-12)) : 0;
            var chosen = gaps.Take(count).ToArray(); double saved = chosen.Sum(g => g.Score);
            var curve = new GapCurve(budget, pageTime * ps / (end-start), saved * ps / (end-start), saved/pageTime, count, accesses == 0 ? 0 : count * recoveryUs / accesses);
            curves.Add(new { aggregate = curve, processes = stats.OrderBy(x => x.Key).Select(x => new { pid = x.Key, accesses = x.Value.Accesses, baseline_mean_bytes = x.Value.PageTime * ps / (end-start), model_mean_saved_bytes = chosen.Where(g => g.Pid == x.Key).Sum(g => g.Score) * ps / (end-start) }).ToArray() });
        }
        return new { schema = "elasticmem.g0.app.v1", status = kind == "synthetic" ? "synthetic_validation_only" : "scenario_model_not_machine_ceiling", trace_kind = kind, boundary_mode = "interior", event_count = events, costs = new { evictionUs, recoveryUs, maxFaultUs }, curves,
            limitations = new[] { "Optimum of independent gap-minus-transfer scoring objective; NOT a universal/demand-paging upper bound", "Time-average mapped bytes, not physical RSS or peak capacity; shared pages counted repeatedly", "Global sum of serial recoveries / page touches is not application slowdown", "No bandwidth contention, reserve, dirty writeback or retimed dependencies; costs are parameters", "Full access collector is external; metadata exact is a source assertion, not completeness proof" } };
    }
}
