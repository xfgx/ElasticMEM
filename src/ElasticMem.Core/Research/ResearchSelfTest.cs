using System.Buffers.Binary;
using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class ResearchSelfTest
{
    public static object Run()
    {
        int checks=0;var errors=new List<string>();void Check(bool ok,string name){checks++;if(!ok)errors.Add(name);}
        var rnd=new Random(19);var store=new PackedAgentStore(1000,new[]{"a","b"});var shadow=Enumerable.Repeat(new AgentRecord(0,0,-1,0),1000).ToArray();
        for(int step=0;step<20000;step++){int i=rnd.Next(1000);if(step%3!=0){var r=new AgentRecord((ushort)(i%2),step%7==0?uint.MaxValue:(uint)rnd.Next(65537),rnd.Next(-1,1000),rnd.NextInt64());store.Set(i,r);shadow[i]=r;}else Check(store.Get(i)==shadow[i],"typed random read "+step);}
        var a=new byte[4096];rnd.NextBytes(a);BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(0,8),0x1010);var b=(byte[])a.Clone();BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0,8),0x5010);
        var snaps=new[]{new ProcessSnapshot(1,1,new[]{new SnapshotRegion(0x1000,4096)},new[]{new SnapshotPage(0x1000,a)}),new ProcessSnapshot(2,2,new[]{new SnapshotRegion(0x5000,4096)},new[]{new SnapshotPage(0x5000,b)})};
        var result=NormalizedDedup.Run(snaps);Check(result.Roundtrip,"normalization roundtrip");Check(result.RawUnique==2&&result.NormalizedUnique==1,"ASLR-like normalization");Check(result.NormalizedDedupBytes>=4096+128,"normalization metadata paid");
        string temp=Path.Combine(Path.GetTempPath(),"ElasticMem_self_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            NormalizedColdStore.Write(Path.Combine(temp,"fixture.emd"),snaps);var restored=NormalizedColdStore.Restore(Path.Combine(temp,"fixture.emd"));Check(restored[0].Pages[0].Bytes.AsSpan().SequenceEqual(a)&&restored[1].Pages[0].Bytes.AsSpan().SequenceEqual(b),"cold container roundtrip");Check(new FileInfo(Path.Combine(temp,"fixture.emd")).Length==result.NormalizedBrotliBytes,"cold footprint exact");
            var container=File.ReadAllBytes(Path.Combine(temp,"fixture.emd"));
            foreach(var broken in new[]{container[..^1],container.Concat(new byte[]{1}).ToArray()})
            {
                File.WriteAllBytes(Path.Combine(temp,"broken.emd"),broken);try{NormalizedColdStore.Restore(Path.Combine(temp,"broken.emd"));Check(false,"corrupt container rejected");}catch(Exception ex)when(ex is InvalidDataException or EndOfStreamException){Check(true,"corrupt container rejected");}
            }
            var invalidRef=(byte[])container.Clone();BinaryPrimitives.WriteInt32LittleEndian(invalidRef.AsSpan(invalidRef.Length-80,4),9);File.WriteAllBytes(Path.Combine(temp,"broken.emd"),invalidRef);
            try{NormalizedColdStore.Restore(Path.Combine(temp,"broken.emd"));Check(false,"invalid ref rejected");}catch(InvalidDataException){Check(true,"invalid ref rejected");}
            string exportRoot=Path.Combine(temp,"export-runs");
            using(var exportRun=new ResearchRun(exportRoot,"test","unit","fixture",new{}))
            {
                File.WriteAllText(exportRun.FilePath("inputs/trace.csv"),"private-data");File.WriteAllText(exportRun.FilePath("misplaced-trace.csv"),"private-data");exportRun.WriteJson("result.json",new{status="fixture"});exportRun.Seal("passed","unit");
                string zip=Path.Combine(temp,"logs.zip");ResearchRun.ExportLogs(exportRoot,zip,false);
                using(var z=System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    Check(z.Entries.All(e=>!e.FullName.Contains("inputs/")&&!e.FullName.EndsWith("misplaced-trace.csv")),"export privacy allowlist");
                    using var index=JsonDocument.Parse(new StreamReader(z.GetEntry("EXPORT.json")!.Open()).ReadToEnd());
                    foreach(var item in index.RootElement.GetProperty("files").EnumerateArray()){using var data=z.GetEntry(item.GetProperty("path").GetString()!)!.Open();string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();Check(hash==item.GetProperty("sha256").GetString(),"export exact-byte hash");}
                }
                File.WriteAllText(Path.Combine(exportRun.DirectoryPath,"result.json"),"tampered");
                try{ResearchRun.ExportLogs(exportRoot,Path.Combine(temp,"tampered.zip"),false);Check(false,"tampered source export refused");}catch(InvalidDataException){Check(true,"tampered source export refused");}
                exportRun.Invalidate(new InvalidDataException("test integrity failure"));using var invalid=JsonDocument.Parse(File.ReadAllText(Path.Combine(exportRun.DirectoryPath,"manifest.json")));Check(invalid.RootElement.GetProperty("status").GetString()=="invalid_integrity_not_result","invalid result persists");ResearchRun.Verify(exportRun.DirectoryPath);
            }
            string trace=Path.Combine(temp,"trace.csv"),meta=Path.Combine(temp,"metadata.json");
            File.WriteAllText(trace,"timestamp_ns,pid,page,op\n0,1,4096,map\n100000,1,4096,read\n900000,1,4096,write\n1000000,1,4096,unmap\n");
            var metadata=new Dictionary<string,object>{{"schema","elasticmem.events.v1"},{"trace_kind","synthetic"},{"source","fixture"},{"scope","unit page"},{"clock","monotonic ns"},{"access_definition","page touches"},{"process_identity","no reuse"},{"event_loss_count",0},{"limitations",Array.Empty<string>()},{"page_size",4096},{"start_ns",0L},{"end_ns",1000000L}};
            File.WriteAllText(meta,JsonSerializer.Serialize(metadata));string json=JsonSerializer.Serialize(GapModel.Run(trace,meta,10,100,100,new[]{0d,50,100}),ResearchRun.Json);using var doc=JsonDocument.Parse(json);
            var curves=doc.RootElement.GetProperty("curves");Check(curves[0].GetProperty("aggregate").GetProperty("recoveries").GetInt64()==0,"zero budget");Check(Math.Abs(curves[1].GetProperty("aggregate").GetProperty("model_saved_fraction").GetDouble()-.69)<1e-10,"gap arithmetic");
            metadata["trace_kind"]="sampled";File.WriteAllText(meta,JsonSerializer.Serialize(metadata));try{GapModel.Run(trace,meta,10,100,100,new[]{0d});Check(false,"sampled rejected");}catch(InvalidDataException){Check(true,"sampled rejected");}
            using(var run=new ResearchRun(temp,"test","unit","fixture",new{})){File.WriteAllText(run.FilePath("raw.txt"),"a");run.Seal("passed","unit");ResearchRun.Verify(run.DirectoryPath);Check(true,"sealed hash verify");File.WriteAllText(Path.Combine(run.DirectoryPath,"raw.txt"),"b");try{ResearchRun.Verify(run.DirectoryPath);Check(false,"tamper rejected");}catch(InvalidDataException){Check(true,"tamper rejected");}}
        }
        finally{Directory.Delete(temp,true);}
        if(errors.Count!=0)throw new InvalidDataException(string.Join("\n",errors));return new{status="selftests_passed",checks,seed=19,limitations=new[]{"Core correctness only; no Windows API/GPU/UI execution"}};
    }
}
