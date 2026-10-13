using System.Numerics;
using System.Text.Json;
namespace ElasticMem.Core.Research;

public static class GroupingBench
{
    public static int[][] Groups(ulong[] addresses,byte[][] pages,bool[][] activity,string policy,CancellationToken ct=default)
    {
        if(addresses.Length<4||pages.Length!=addresses.Length||pages.Any(p=>p.Length!=4096)||activity.Length<8||activity.Any(a=>a.Length!=pages.Length)||addresses.Distinct().Count()!=addresses.Length)
            throw new ArgumentException("Grouping input shape/unique addresses; at least8epochs");
        int split=activity.Length/2;
        // Only the training prefix enters the learned signature. At most64 prefix epochs
        // are used (spread over the prefix); no suffix activity is consulted.
        ulong[] signature=new ulong[pages.Length];
        int trained=Math.Min(64,split);
        for(int p=0;p<pages.Length;p++)for(int t=0;t<trained;t++)
            if(activity[t*split/trained][p])signature[p]|=1UL<<t;
        int[] order=Enumerable.Range(0,pages.Length).OrderBy(i=>addresses[i]).ToArray();
        if(policy=="content")
            order=order.OrderBy(i=>ContentKey(pages[i])).ThenBy(i=>addresses[i]).ToArray();
        else if(policy!="adjacency"&&policy!="coaccess")throw new ArgumentException("Grouping policy");
        if(policy!="coaccess")return order.Chunk(4).ToArray();
        var remaining=new HashSet<int>(order);var groups=new List<int[]>();
        foreach(int first in order)
        {
            ct.ThrowIfCancellationRequested();
            if(!remaining.Remove(first))continue;
            var g=new List<int>{first};
            foreach(int next in remaining.OrderBy(i=>BitOperations.PopCount(signature[first]^signature[i])).ThenBy(i=>addresses[i]).Take(3).ToArray())
            {g.Add(next);remaining.Remove(next);}
            groups.Add(g.ToArray());
        }
        return groups.ToArray();
    }
    static ulong ContentKey(byte[] page)
    {
        var histogram=new int[256];foreach(byte b in page)histogram[b]++;
        int dominant=Enumerable.Range(0,256).OrderByDescending(i=>histogram[i]).ThenBy(i=>i).First();
        return ((ulong)dominant<<48)|((ulong)histogram[dominant]<<32)|((ulong)histogram[0]<<16)|(uint)histogram[255];
    }
    public static object Utility(int[][] groups,bool[][] activity,int windowEpochs=1,bool[][]? known=null,CancellationToken ct=default)
    {
        if(windowEpochs<0||windowEpochs>16||activity.Length<8||activity.Any(a=>a.Length!=activity[0].Length)||
           groups.Any(g=>g.Length is <1 or >4)||groups.SelectMany(g=>g).Order().SequenceEqual(Enumerable.Range(0,activity[0].Length))==false)
            throw new ArgumentException("Utility partition/shape");
        known??=activity.Select(a=>Enumerable.Repeat(true,a.Length).ToArray()).ToArray();
        if(known.Length!=activity.Length||known.Any(a=>a.Length!=activity[0].Length))throw new ArgumentException("Known-state shape");
        int split=activity.Length/2;var useful=new List<double>();long decoded=0;int unknownWindows=0;
        foreach(var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            int coveredUntil=split-1;
            for(int t=split;t<activity.Length;t++)
            {
                if(t<=coveredUntil)continue;
                int finish=Math.Min(activity.Length-1,t+windowEpochs);
                if(g.Any(p=>Enumerable.Range(t,finish-t+1).Any(e=>!known[e][p]))){unknownWindows++;coveredUntil=finish;continue;}
                if(!g.Any(p=>activity[t][p]))continue;
                // All distinct useful pages count, including the triggering epoch.
                useful.Add(g.Count(p=>Enumerable.Range(t,finish-t+1).Any(e=>activity[e][p])));
                decoded+=g.Length;coveredUntil=finish;
            }
        }
        return new{training_epochs=split,test_epochs=activity.Length-split,signature_training_epochs=Math.Min(64,split),
            following_window_epochs=windowEpochs,trigger_epoch_included=true,decode_events=useful.Count,unknown_windows_excluded=unknownWindows,
            mean_useful_pages_per_decode=useful.Count>0?(double?)useful.Average():null,
            p50_useful_pages=useful.Count>0?(double?)CodecBench.Quantile(useful,.5):null,
            p95_useful_pages=useful.Count>0?(double?)CodecBench.Quantile(useful,.95):null,
            decoded_pages=decoded,useful_pages=useful.Sum(),
            useful_fraction_of_decoded=decoded>0?(double?)(useful.Sum()/decoded):null};
    }
    public static object Run(CodecInput source,bool[][] activity,string output,CancellationToken ct,bool[][]? known=null)
    {
        if(known!=null&&(known.Length!=activity.Length||known.Any(a=>a.Length!=source.Pages.Length)||known.Take(activity.Length/2).Any(a=>a.Any(x=>!x))))throw new ArgumentException("Cohort must be TRAIN-known only");
        Directory.CreateDirectory(output);
        var results=new List<object>();double? baseline=null,coaccess=null;
        foreach(string policy in new[]{"adjacency","coaccess","content"})
        {
            ct.ThrowIfCancellationRequested();int[][] groups=Groups(source.Addresses,source.Pages,activity,policy,ct);
            var flat=groups.SelectMany(g=>g).ToArray();string path=Path.Combine(output,policy+".ecb");
            using var codec=new PortablePageCodec("zstd1");
            using(var stream=File.Create(path))using(var w=new BinaryWriter(stream))
            {
                w.Write(0x31424345);w.Write(codec.Name);w.Write(16384);w.Write(flat.Length);w.Write(groups.Length);w.Write(0);
                for(int i=0;i<flat.Length;i++){w.Write(source.Pid);w.Write(0);w.Write(source.Addresses[flat[i]]);w.Write(i/4);w.Write((i%4)*4096);}
                foreach(var group in groups)
                {
                    ct.ThrowIfCancellationRequested();byte[] raw=group.SelectMany(i=>source.Pages[i]).ToArray();
                    byte[] compressed=codec.Encode(raw);bool fallback=compressed.Length>=raw.Length;byte[] stored=fallback?raw:compressed;
                    w.Write(raw.Length);w.Write(stored.Length);w.Write(fallback);w.Write(new byte[7]);w.Write(stored);w.Write(new byte[((stored.Length+7)&~7)-stored.Length]);
                }
            }
            CodecBench.VerifyStore(path,codec,flat.Select(i=>source.Pages[i]).ToArray(),ct,
                flat.Select(_=>source.Pid).ToArray(),flat.Select(i=>source.Addresses[i]).ToArray());
            using var utility=JsonDocument.Parse(JsonSerializer.Serialize(Utility(groups,activity,1,known,ct)));
            double? mean=utility.RootElement.GetProperty("mean_useful_pages_per_decode").GetDoubleOrNull();
            if(policy=="adjacency")baseline=mean;if(policy=="coaccess")coaccess=mean;
            long serialized=new FileInfo(path).Length,allocated=(serialized+255)/256*256,reserve=((allocated*8+99)/100+255)/256*256;
            results.Add(new{policy,pages=flat.Length,serialized_bytes=serialized,total_with_reserve_bytes=allocated+reserve,
                ratio_raw_to_store=(double)flat.Length*4096/(allocated+reserve),serialized_restore_verified=true,utility=utility.RootElement.Clone()});
        }
        return new{schema="elasticmem.g5.v1",status="offline_grouping_proxy_not_pager",pid=source.Pid,pages=source.Pages.Length,policies=results,
            unknown_suffix_cells=known==null?0:known.Skip(activity.Length/2).Sum(a=>a.Count(x=>!x)),
            continuation_gate=coaccess.HasValue&&baseline.HasValue&&(known==null||known.Skip(activity.Length/2).All(a=>a.All(x=>x)))?(bool?)(coaccess>=2&&coaccess>baseline):null,
            limitations=new[]{"Frozen per-process groups of up to4pages; train prefix only,at most64 spread prefix epochs; held-out suffix",
            "N=1 following epoch plus trigger epoch; no repeated decode while within that window; no real cache capacity policy",
            "Post-trim residency reacquisition,not exact access or actual page misses; cohort selected from known TRAIN prefix only; unknown suffix windows reported/excluded,gate unavailable with any unknown suffix",
            "Content grouping is static dominant-byte/zero/FF histogram heuristic,not a trained semantic model",
            "Ratio uses one pre-trace snapshot; no churn,free/reuse/write events; mappings/headers/alignment/reserve counted,workspace/RSS not",
            "No physical reclaim,driver,pagefile or novelty claim; absolute2 AND improvement over adjacency required"}};
    }
    static double? GetDoubleOrNull(this JsonElement e)=>e.ValueKind==JsonValueKind.Null?null:e.GetDouble();
}