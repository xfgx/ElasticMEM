namespace ElasticMem.Core.Research;
public static class ChurnBench
{
    public static object Run(CancellationToken ct)
    {
        // Controlled OWN fixture, not a model fitted to renderer churn.
        var policies=new List<object>();
        foreach(int group in new[]{1,4})
        {
            using var codec=new PortablePageCodec("zstd1");var pages=new byte[64][];var alive=new bool[64];var stores=new Dictionary<int,(byte[] Blob,bool Raw)>();
            var rng=new Random(1704);long rewritten=0;var sizes=new List<long>();var dead=new List<int>();int reads=0;
            for(int step=0;step<256;step++)
            {
                ct.ThrowIfCancellationRequested();int page=rng.Next(64),block=page/group;
                if(step%3==0&&alive[page])
                {
                    alive[page]=false;if(!Enumerable.Range(block*group,group).Any(i=>alive[i]))stores.Remove(block);
                    // Otherwise dead slot remains until another write repacks the block.
                }
                else
                {
                    pages[page]=new byte[4096];for(int i=0;i<4096;i++)pages[page][i]=(byte)(i%64);pages[page][0]=(byte)page;pages[page][1]=(byte)step;alive[page]=true;
                    byte[] raw=Enumerable.Range(block*group,group).SelectMany(i=>pages[i]??new byte[4096]).ToArray();
                    byte[] packed=codec.Encode(raw);bool fallback=packed.Length>=raw.Length;stores[block]=(fallback?raw:packed,fallback);rewritten+=stores[block].Blob.Length;
                }
                long size=64*24+32;int deadSlots=0;
                foreach(var pair in stores)
                {
                    var (blob,fallback)=pair.Value;byte[] restored=fallback?(byte[])blob.Clone():codec.Decode(blob,group*4096);
                    for(int j=0;j<group;j++){int at=pair.Key*group+j;if(alive[at]){if(!restored.AsSpan(j*4096,4096).SequenceEqual(pages[at]))throw new InvalidDataException("Churn live restore mismatch");reads++;}else deadSlots++;}
                    size+=16+((blob.Length+7)&~7);
                }
                sizes.Add(size);dead.Add(deadSlots);
            }
            policies.Add(new{block_bytes=group*4096,steps=256,verified_live_page_reads=reads,payload_bytes_rewritten=rewritten,
                mean_serialized_bytes=sizes.Average(),max_serialized_bytes=sizes.Max(),mean_dead_logical_slots=dead.Average(),max_dead_logical_slots=dead.Max(),final_live_pages=alive.Count(x=>x)});
        }
        return new{status="own_synthetic_churn_not_renderer_evidence",policies,limitations=new[]{"Same seeded64page/256operation own alloc/free/write schedule; adjacent fixed blocks; freed slot retained until block disappears",
            "Serial footprint includes24byte page mappings,16byte block headers,8byte padding; allocation/RSS/reserve/compaction not measured",
            "Decode all live slots after each operation verifies bytes; no syscall/pagefile latency or real workload churn; cannot decide driver viability"}};
    }
}