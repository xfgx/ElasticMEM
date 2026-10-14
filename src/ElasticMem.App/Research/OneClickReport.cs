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
            string? expected=mode switch{"controlled"=>"controlled_synthetic_validation_not_browser_evidence","epochs-replay"=>"offline_epoch_replay_not_physical_ram","g0-calibration"=>"controlled_own_child_workload_only","g0-epochs"=>"perturbing_epoch_proxy_not_exact_trace","g4"=>"offline_codec_comparison_not_live_reclaim","g3-dma"=>"dma_microbenchmark_not_pager","disk-qd1"=>"unbuffered_file_qd1_not_pagefile_fault",_=>null};
            if(expected==null)continue;
            string? status=manifest.RootElement.GetProperty("status").GetString();
            if(status!=expected){lines.Add(mode+": "+status+"; числа не считаются валидными.");continue;}
            ElasticMem.Core.Research.ResearchRun.Verify(folder);
            string file=Path.Combine(folder,"result.json");if(!File.Exists(file))throw new InvalidDataException("Successful run has no result");
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var d=doc.RootElement;
            if(!d.TryGetProperty("schema",out var schema))throw new InvalidDataException("Successful result schema missing");
            string? expectedSchema=mode switch{"controlled"=>"elasticmem.controlled.research.v1","epochs-replay"=>"elasticmem.epochs.replay.v1","g0-calibration"=>"elasticmem.g0.calibration.v2","g0-epochs"=>"elasticmem.g0.windows_epochs.v2","g4"=>"elasticmem.g4.codecs.v1","g3-dma"=>"elasticmem.g3.dma.v1","disk-qd1"=>"elasticmem.disk.qd1.v1",_=>null};
            if(schema.GetString()!=expectedSchema&&!(mode=="controlled"&&schema.GetString()=="elasticmem.controlled.research.v2"))throw new InvalidDataException("Successful result schema mismatch");
            switch(schema.GetString())
            {
                case "elasticmem.controlled.research.v1":
                case "elasticmem.controlled.research.v2":
                    if(schema.GetString()=="elasticmem.controlled.research.v2"&&!d.TryGetProperty("lifecycle",out _))throw new InvalidDataException("Controlled v2 lifecycle missing");
                    lines.Add("Контроль известного доступа: только собственная native arena; НЕ реальный renderer,НЕ RAM savings.");
                    foreach(var c in d.GetProperty("cases").EnumerateArray())
                    {
                        var ob=c.GetProperty("observation");
                        lines.Add("  "+c.GetProperty("name")+": known cells="+N(100*ob.GetProperty("known_fraction").GetDouble())+"%; unknown TEST epochs="+ob.GetProperty("unknown_test_epochs")+"; FN="+ob.GetProperty("known_false_negative")+"; FP="+ob.GetProperty("known_false_positive")+" (только known cells).");
                        foreach(string kind in new[]{"ground_truth_causal","windows_proxy_causal"})
                            foreach(var row in c.GetProperty(kind).GetProperty("rows").EnumerateArray().Where(x=>x.GetProperty("idle_epochs").GetInt32()==2&&x.GetProperty("block_pages").GetInt32() is 4 or 16))
                            {
                                var utility=row.GetProperty("mean_useful_pages_per_decode");
                                lines.Add("    "+kind+" T2/cap"+row.GetProperty("block_pages")+" "+row.GetProperty("policy")+": "+(utility.ValueKind==JsonValueKind.Null?"unavailable":N(utility.GetDouble()))+" useful/decode; events="+row.GetProperty("decode_events")+"; unknown="+row.GetProperty("excluded_common_unknown_epochs")+".");
                            }
                    }
                    if(d.TryGetProperty("lifecycle",out var life))
                    {
                        lines.Add("  Lifecycle decommit/recommit: active="+life.GetProperty("active_page_epoch_cells")+"; dead="+life.GetProperty("dead_page_epoch_cells")+"; reserved-only="+life.GetProperty("reserved_never_committed_page_epoch_cells")+"; retained unknown="+life.GetProperty("retained_active_unknown_cells")+"; FN/FP="+life.GetProperty("known_false_negative")+"/"+life.GetProperty("known_false_positive")+" (known active only).");
                        lines.Add("  Commits/decommits="+life.GetProperty("committed_pages")+"/"+life.GetProperty("decommitted_pages")+"; dead reported Valid before/after="+life.GetProperty("dead_reported_valid_before")+"/"+life.GetProperty("dead_reported_valid_after")+"; reserved-only Valid before/after="+life.GetProperty("reserved_reported_valid_before")+"/"+life.GetProperty("reserved_reported_valid_after")+"; checksum="+life.GetProperty("checksum_verified")+". Unknown НЕ death; поколения не смешиваются. Г5 lifecycle skipped,не поддерживает death.");
                    }
                    lines.Add("  Синтетический positive control не подтверждает пользу в Chrome. Primary T2/cap4,16 фиксирован; прочие T exploratory. Реальные G5 gates НЕ ослаблены.");
                    break;
                case "elasticmem.epochs.replay.v1":
                    foreach(var p in d.GetProperty("processes").EnumerateArray())
                    {
                        var diag=p.GetProperty("diagnostic");
                        lines.Add("Офлайн PID "+p.GetProperty("pid")+": baseline="+p.GetProperty("baseline_residency_available")+"; primary cohort="+diag.GetProperty("primary_cohort")+"; GC/Belady/дополнительная RAM unavailable.");
                        var model=diag.GetProperty("model");
                        if(model.TryGetProperty("scenario_best",out var best))
                        {
                            double budget=model.GetProperty("budget_us_per_second").GetDouble();
                            lines.Add("  Г0 средний бюджет "+N(budget)+" мкс/с; НЕ предел всплеска. Конечное окно включает startup/censoring.");
                            foreach(var scenario in best.EnumerateArray())
                            {
                                double cost=scenario.GetProperty("miss_cost_us").GetDouble(),bytes=scenario.GetProperty("maximum_among_tested_thresholds_bytes").GetDouble();
                                lines.Add("  Сценарий "+N(cost)+" мкс: "+N(bytes/1048576)+" МиБ виртуальной модели среди проверенных T,НЕ физическая экономия RAM.");
                                var eligible=model.GetProperty("curves").EnumerateArray().Where(c=>c.GetProperty("modeled_reacquisitions_per_second").GetDouble()*cost<=budget).OrderByDescending(c=>c.GetProperty("average_end_epoch_offloaded_bytes").GetDouble()).ToArray();
                                if(eligible.Length>0){var c=eligible[0];lines.Add("    T="+c.GetProperty("idle_epochs")+"; модельная стоимость эпохи p99/max="+N(c.GetProperty("misses_per_epoch_p99").GetDouble()*cost/1000)+"/"+N(c.GetProperty("misses_per_epoch_max").GetDouble()*cost/1000)+" мс,НЕ измеренные задержки.");}
                            }
                        }
                        var causal=p.GetProperty("causal");
                        if(causal.TryGetProperty("rows",out var rows))
                        {
                            var rr=rows.EnumerateArray().ToArray();
                            var unknown=rr.Select(x=>x.GetProperty("excluded_common_unknown_epochs").GetInt32()).Distinct().ToArray();
                            if(unknown.Length!=1)throw new InvalidDataException("Causal common unknown epoch inconsistency");
                            int accepted=rr.Count(x=>x.GetProperty("gate_same_epoch_only").ValueKind==JsonValueKind.True);
                            lines.Add("  Г5 TRAIN cohort="+p.GetProperty("causal_cohort_pages")+"; unknown TEST epochs="+unknown[0]+"/"+rr[0].GetProperty("test_epochs")+"; "+(unknown[0]>0?"gate unavailable — НЕ отрицательный результат.":"положительных exploratory rows="+accepted+"; НЕ независимое подтверждение."));
                            lines.Add("  CPU — первые ≤128 warmed decode/copy событий; НЕ total paging cost. Полные causal1/4/8/16 — result.json.");
                        }
                        else lines.Add("  Г5: "+causal.GetProperty("status"));
                        lines.Add("  Причины unknown: "+(p.TryGetProperty("trace_health_available",out var available)&&available.GetBoolean()?"trace_health/result.json + исходный epoch-health.csv; census/snapshot intersection,НЕ selected G5.":"нет в старой трассе; replay их не восстанавливает."));
                    }
                    break;
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
                    var codecs=d.GetProperty("codecs").EnumerateArray().ToArray();
                    var plain=codecs.Where(c=>c.GetProperty("codec").GetString()=="xpress-4096"&&c.TryGetProperty("total_with_reserve_bytes",out _)).ToArray();
                    foreach(var c in codecs)if(c.TryGetProperty("total_with_reserve_bytes",out var size))
                    {
                        double bytes=size.GetInt64();
                        if(bytes<=0)throw new InvalidDataException("Invalid codec store size");
                        lines.Add("Г4 "+c.GetProperty("codec").GetString()+": "+N(bytes/1048576d)+" МиБ (с резервом).");
                        if(plain.Length==1&&c.GetProperty("codec").GetString()!="xpress-4096")
                        {
                            double baseline=plain[0].GetProperty("total_with_reserve_bytes").GetInt64();
                            if(baseline<=0)throw new InvalidDataException("Invalid plain Xpress baseline");
                            double saving=100*(1-bytes/baseline);
                            lines.Add("  Меньше plain Xpress: "+N(saving)+"%; порог размера ≥25% "+(saving>=25?"взят":"НЕ взят")+" в этой выборке; НЕ сравнение со штатным Windows store.");
                        }
                        lines.Add("  Восстановление stored payload p50/block="+N(c.GetProperty("stored_payload_restore_p50_us_per_block").GetDouble())+" мкс; блок="+c.GetProperty("block_bytes")+" байт,НЕ fault/stack latency.");
                    }
                    lines.Add("Г4: 16 КиБ восстанавливаются целиком для запроса одной страницы; меньший store не доказывает лучшую random-access latency.");
                    var zstd=codecs.Where(c=>c.GetProperty("codec").GetString()=="zstd1-4096"&&c.TryGetProperty("total_with_reserve_bytes",out _)).ToArray();
                    var dict=codecs.Where(c=>c.GetProperty("codec").GetString()=="zstd1-dictionary-4096"&&c.TryGetProperty("total_with_reserve_bytes",out _)).ToArray();
                    if(zstd.Length==1&&dict.Length==1)lines.Add("Словарь: изменение размера относительно plain zstd4K="+N(100*(dict[0].GetProperty("total_with_reserve_bytes").GetDouble()/zstd[0].GetProperty("total_with_reserve_bytes").GetDouble()-1))+"% (положительное = хуже).");
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
            {
                lines.Add("  GPU "+v.GetProperty("mode")+": p50="+N(v.GetProperty("p50_us").GetDouble())+" мкс, p95="+N(v.GetProperty("p95_us").GetDouble())+" мкс.");
                double gpuMedian=v.GetProperty("p50_us").GetDouble();
                lines.Add("  Отношение FILE/GPU медиан="+N(median/gpuMedian)+"×; четверть FILE median "+(gpuMedian<=median/4?"взята":"НЕ взята")+" только для "+v.GetProperty("mode")+"; НЕ pagefile/tail-latency/pager gate.");
            }
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