using System.Globalization;
using System.Text.Json;
namespace ElasticMem.App.Research;
internal static class OneClickReport
{
    static string N(double n)=>n.ToString("F2",CultureInfo.InvariantCulture);
    public static string Read(string root,List<string>? errors=null,string? openSummary=null)
    {
        var lines=new List<string>{"\r\nЧИСЛА ПРОГОНА (не экономия RAM):"};
        JsonElement? disk=null,dma=null;
        foreach(string folder in Directory.GetDirectories(root).Order())
        {
            if(openSummary!=null&&Path.GetFullPath(folder).Equals(Path.GetFullPath(openSummary),StringComparison.OrdinalIgnoreCase))continue;
            int beforeLines=lines.Count;
            try
            {
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));
            string? mode=manifest.RootElement.GetProperty("mode").GetString();
            string? expected=mode switch{"g0-calibration"=>"controlled_own_child_workload_only","g0-epochs"=>"perturbing_epoch_proxy_not_exact_trace","g4"=>"offline_codec_comparison_not_live_reclaim","g3-dma"=>"dma_microbenchmark_not_pager","disk-qd1"=>"unbuffered_file_qd1_not_pagefile_fault",_=>null};
            if(expected==null)continue;
            string? status=manifest.RootElement.GetProperty("status").GetString();
            if(status!=expected){lines.Add(mode+": "+status+"; числа не считаются валидными.");continue;}
            ElasticMem.Core.Research.ResearchRun.Verify(folder);
            string file=Path.Combine(folder,"result.json");if(!File.Exists(file))throw new InvalidDataException("Successful run has no result");
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var d=doc.RootElement;
            if(!d.TryGetProperty("schema",out var schema))throw new InvalidDataException("Successful result schema missing");
            string? expectedSchema=mode switch{"g0-calibration"=>"elasticmem.g0.calibration.v2","g0-epochs"=>"elasticmem.g0.windows_epochs.v2","g4"=>"elasticmem.g4.codecs.v1","g3-dma"=>"elasticmem.g3.dma.v1","disk-qd1"=>"elasticmem.disk.qd1.v1",_=>null};
            if(schema.GetString()!=expectedSchema)throw new InvalidDataException("Successful result schema mismatch");
            switch(schema.GetString())
            {
                case "elasticmem.g0.calibration.v2":
                    foreach(string phase in new[]{"trim","trim_query"})
                    {
                        var interval=d.GetProperty("paired_intervals").GetProperty(phase);
                        lines.Add("Г0 own-child "+phase+": "+N(interval.GetProperty("mean_paired_change_pct").GetDouble())+"%; bootstrap95% ["+N(interval.GetProperty("bootstrap_percentile_95_low").GetDouble())+"; "+N(interval.GetProperty("bootstrap_percentile_95_high").GetDouble())+"] (описательный, не causal).");
                    }
                    break;
                case "elasticmem.disk.qd1.v1":ValidateGroups(d);disk=d.Clone();break;
                case "elasticmem.g3.dma.v1":ValidateGroups(d);dma=d.Clone();break;
                case "elasticmem.g4.codecs.v1":
                    foreach(var c in d.GetProperty("codecs").EnumerateArray())if(c.TryGetProperty("total_with_reserve_bytes",out var size))
                        lines.Add("Г4 "+c.GetProperty("codec").GetString()+": "+N(size.GetInt64()/1048576d)+" МиБ (с резервом).");
                    break;
                case "elasticmem.g0.windows_epochs.v2":
                    foreach(var p in d.GetProperty("processes").EnumerateArray())
                    {
                        lines.Add($"Г0 PID {p.GetProperty("pid")}: inventory={p.GetProperty("inventory_committed_readable_private_pages")}, selected={p.GetProperty("sampled_pages")}, model-known={p.GetProperty("model_included_pages")}; НЕ потолок всей машины.");
                        var model=p.GetProperty("model");
                        if(model.TryGetProperty("curves",out var curves))foreach(var c in curves.EnumerateArray())
                            lines.Add("  T="+c.GetProperty("idle_epochs")+": V="+N(c.GetProperty("average_end_epoch_offloaded_bytes").GetDouble()/1048576)+" МиБ виртуальной модели; R="+N(c.GetProperty("modeled_reacquisitions_per_second").GetDouble())+"/с.");
                        var g=p.GetProperty("g5");
                        if(g.TryGetProperty("policies",out var policies))foreach(var policy in policies.EnumerateArray())
                        {
                            var mean=policy.GetProperty("utility").GetProperty("mean_useful_pages_per_decode");
                            lines.Add("  Г5 "+policy.GetProperty("policy")+": полезных страниц/распаковку="+(mean.ValueKind==JsonValueKind.Null?"не измерено":N(mean.GetDouble()))+", размер="+policy.GetProperty("total_with_reserve_bytes")+" байт; unknown-suffix="+g.GetProperty("unknown_suffix_cells"));
                        }
                        if(g.TryGetProperty("continuation_gate",out var gate))lines.Add("  Г5 gate="+gate+" (null = недостаточно известных данных; proxy, не live paging).");
                    }
                    break;
            }
            }
            catch(Exception ex)
            {
                lines.RemoveRange(beforeLines,lines.Count-beforeLines);
                string error=Path.GetFileName(folder)+": "+ex.GetType().Name;
                errors?.Add(error);lines.Add("Числовая сводка недоступна: "+error+"; исходный статус/диагностика сохранены.");
            }
        }
        if(disk.HasValue)foreach(var g in disk.Value.GetProperty("groups").EnumerateArray())
        {
            int bytes=g.GetProperty("bytes").GetInt32();double median=g.GetProperty("p50_us").GetDouble();
            lines.Add($"Диск QD1 {bytes/1024} КиБ: p50={N(median)} мкс; НЕ pagefile-fault/NVMe подтверждение.");
            if(dma.HasValue)foreach(var v in dma.Value.GetProperty("groups").EnumerateArray().Where(v=>v.GetProperty("bytes").GetInt32()==bytes))
                lines.Add("  GPU "+v.GetProperty("mode")+": p50="+N(v.GetProperty("p50_us").GetDouble())+" мкс, p95="+N(v.GetProperty("p95_us").GetDouble())+" мкс.");
        }
        lines.Add("Подтверждённые latency Xpress/pagefile, payload/ratio штатного хранилища и дополнительный ≥1 ГБ: unavailable. Не подменять их QD1, WS или моделью.");
        return string.Join("\r\n",lines)+"\r\n";
    }
    static void ValidateGroups(JsonElement result)
    {
        var groups=result.GetProperty("groups").EnumerateArray().ToArray();if(groups.Length==0)throw new InvalidDataException("Missing timing groups");
        foreach(var g in groups)
        {
            int bytes=g.GetProperty("bytes").GetInt32(),n=g.GetProperty("n").GetInt32();
            double p50=g.GetProperty("p50_us").GetDouble(),p95=g.GetProperty("p95_us").GetDouble();
            if(bytes is not (4096 or 65536 or 262144)||n<10||!double.IsFinite(p50)||!double.IsFinite(p95)||p50<=0||p95<p50)throw new InvalidDataException("Invalid timing group");
        }
    }
}