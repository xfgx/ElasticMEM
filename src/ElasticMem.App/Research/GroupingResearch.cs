using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class GroupingResearch
{
    public static object Run(string source,ResearchRun run,CancellationToken ct)
    {
        ResearchRun.Verify(source);
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(source,"manifest.json")));
        if(manifest.RootElement.GetProperty("mode").GetString()!="g0-epochs"||manifest.RootElement.GetProperty("status").GetString()!="perturbing_epoch_proxy_not_exact_trace")
            throw new ArgumentException("Successful0.17+ G0 epochs folder required");
        var results=new List<object>();
        foreach(string file in Directory.GetFiles(Path.Combine(source,"inputs"),"epochs-*.eet").Order())
        {
            ct.ThrowIfCancellationRequested();var trace=EpochTraceFile.Read(run.CopyInput(file,Path.GetFileName(file)));
            string inventory=Path.Combine(source,"inputs","inventory-"+trace.Pid+".json");
            string bytes=Path.Combine(source,"inputs","pages-"+trace.Pid+".bin");
            if(new FileInfo(bytes).Length>64L<<20)throw new InvalidDataException("Snapshot cap");
            using var inv=JsonDocument.Parse(File.ReadAllText(run.CopyInput(inventory,Path.GetFileName(inventory))));
            if(inv.RootElement.GetProperty("pid").GetInt32()!=trace.Pid||inv.RootElement.GetProperty("start_ticks").GetInt64()!=trace.Start)throw new InvalidDataException("Epoch/snapshot identity mismatch");
            var raw=File.ReadAllBytes(run.CopyInput(bytes,Path.GetFileName(bytes)));var items=inv.RootElement.GetProperty("pages").EnumerateArray().ToArray();
            if(items.Length*4096L!=raw.Length||items.Where((p,i)=>p.GetProperty("file_offset").GetInt64()!=i*4096L).Any())throw new InvalidDataException("Snapshot offsets");
            var index=trace.Addresses.Select((a,i)=>(a,i)).ToDictionary(x=>x.a,x=>x.i);
            int[] match=Enumerable.Range(0,items.Length).Where(i=>index.TryGetValue(items[i].GetProperty("address").GetUInt64(),out int j)&&trace.KnownRows.Take(trace.Activity.Length/2).All(a=>a[j])).Take(4096).ToArray();
            if(match.Length<4){results.Add(new{pid=trace.Pid,status="skipped_no_complete_snapshot_intersection"});continue;}
            ulong[] addresses=match.Select(i=>items[i].GetProperty("address").GetUInt64()).ToArray();
            results.Add(GroupingBench.Run(new(trace.Pid,trace.Start,addresses,match.Select(i=>raw.AsSpan(i*4096,4096).ToArray()).ToArray()),
                trace.Activity.Select(a=>addresses.Select(address=>a[index[address]]).ToArray()).ToArray(),run.FilePath("inputs/g5-"+trace.Pid),ct,trace.KnownRows.Select(a=>addresses.Select(address=>a[index[address]]).ToArray()).ToArray()));
        }
        if(results.Count==0)throw new ArgumentException("No private EET traces found; logs-only ZIP is insufficient");
        return new{status="offline_grouping_proxy_not_pager",processes=results,limitations=new[]{"Local verified epoch/snapshot input only; no new foreign reads/trim","Cohort filtered by TRAIN-known states only; unknown test windows retained as unknown; no positive gate with unknown suffix","Original snapshot can change during subsequent epochs; no semantic/lifetime atomicity guarantee"}};
    }
}