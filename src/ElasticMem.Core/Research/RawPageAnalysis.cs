using System.Security.Cryptography;
namespace ElasticMem.Core.Research;
public static class RawPageAnalysis
{
    public static object Run(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        // Collision buckets compare actual bytes; SHA equality alone never determines deduplication.
        var blocks=new List<byte[]>();var hash=new Dictionary<string,List<int>>();var membership=new Dictionary<int,HashSet<int>>();var per=new List<object>();int total=0,zeros=0,within=0;
        for(int p=0;p<snapshots.Count;p++)
        {
            var ids=new HashSet<int>();int localZeros=0;
            foreach(var page in snapshots[p].Pages)
            {
                if(page.Bytes.Length!=4096)throw new InvalidDataException("Page analysis size");total++;if(page.Bytes.AsSpan().IndexOfAnyExcept((byte)0)<0){zeros++;localZeros++;}
                string key=Convert.ToHexString(SHA256.HashData(page.Bytes));if(!hash.TryGetValue(key,out var bucket)){bucket=new();hash[key]=bucket;}
                int id=bucket.FirstOrDefault(i=>blocks[i].AsSpan().SequenceEqual(page.Bytes),-1);if(id<0){id=blocks.Count;blocks.Add(page.Bytes);bucket.Add(id);membership[id]=new();}ids.Add(id);membership[id].Add(p);
            }
            within+=snapshots[p].Pages.Length-ids.Count;per.Add(new{pid=snapshots[p].Pid,pages=snapshots[p].Pages.Length,unique_pages=ids.Count,zero_pages=localZeros});
        }
        int cross=membership.Values.Sum(s=>s.Count-1);if(within+cross!=total-blocks.Count)throw new InvalidDataException("Page decomposition mismatch");
        return new{input_pages=total,global_unique_pages=blocks.Count,duplicates_within_processes=within,additional_cross_process_copies=cross,cross_process_payload_bytes=cross*4096L,cross_process_payload_fraction=total==0?0d:(double)cross/total,zero_pages=zeros,zero_duplicate_copies=Math.Max(0,zeros-1),per_process=per,limitations=new[]{"Counts of equal virtual page bytes, not physical frames or actual RAM reclaimed","Payload decomposition excludes codec and metadata costs","Zero-only raw input is not free: measured containers still pay refs, headers and compressed blocks"}};
    }
}
