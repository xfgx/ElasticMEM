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
        var invalidState=PageResidency.Decode((1UL<<15)|(7UL<<1));Check(!invalidState.Valid&&invalidState.Shareable==null&&invalidState.ShareCount==null,"invalid residency fields not interpreted");
        Check(PageEligibility.Eligible(PageEligibility.Private,PageResidency.Decode(3)),"resident exclusive eligible");
        Check(!PageEligibility.Eligible(PageEligibility.Private,PageResidency.Decode(0)),"nonresident never eligible");
        Check(!PageEligibility.Eligible(PageEligibility.Image,PageResidency.Decode(3)),"image excluded");
        Check(!PageEligibility.Eligible(PageEligibility.Mapped,PageResidency.Decode(3)),"mapped excluded");
        Check(!PageEligibility.Eligible(PageEligibility.Private,PageResidency.Decode(3|(1UL<<15))),"shareable excluded even with count one");
        Check(!PageEligibility.Eligible(PageEligibility.Private,PageResidency.Decode(1|(2UL<<1))),"sharecount greater than one excluded");
        Check(PageResidency.Decode(1|(7UL<<1)).ShareCount==7,"sharecount saturated value");
        Check(PageMapping.SameAllocationRange(0x2000,4096,0x1000,0x5000,0x1000,0x2000,0x4000,0x1000),"VirtualQueryEx interior tail accepted");
        Check(PageMapping.SameAllocationRange(0x5000,4096,0x1000,0x5000,0x1000,0x5000,4096,0x1000),"last full interior page accepted");
        Check(PageMapping.SameAllocationRange(0x1000,4096,0x1000,0x5000,0x1000,0x1000,0x5000,0x1000),"region beginning accepted");
        Check(!PageMapping.SameAllocationRange(0x2000,4096,0x1000,0x5000,0x1000,0x2000,0x4000,0x2000),"changed allocation excluded");
        Check(!PageMapping.SameAllocationRange(0x2000,4096,0x1000,0x5000,0x1000,0x3000,0x3000,0x1000),"queried range excludes page");
        Check(!PageMapping.SameAllocationRange(0x6000,4096,0x1000,0x5000,0x1000,0x6000,4096,0x1000),"original range excludes page");
        Check(!PageMapping.Contains(0x1000,0x1800,0x2000,4096),"partial page excluded");
        Check(!PageMapping.Contains(0x1000,0x5000,0x0000,4096),"address below range excluded");
        Check(!PageMapping.Contains(0x1000,0,0x1000,4096),"empty region excluded");
        Check(!PageMapping.Contains(0x1000,0x5000,0x1000,0),"empty page excluded");
        var candidates=Enumerable.Range(10,6).Select(pid=>new ResearchCandidate(pid,"chrome",pid,pid*1024,true,true,true)).ToArray();
        Check(BatchPolicy.Select(candidates,99).Length==4,"auto process cap");
        Check(BatchPolicy.Select(candidates,15).All(c=>c.Pid!=15),"auto self excluded");
        Check(BatchPolicy.Select(new[]{candidates[0]},99).Length==0,"auto needs pair for cross process evidence");
        Check(BatchPolicy.Select(new[]{candidates[0]},99,minimum:1).Length==1,"single process counter diagnostic");
        Check(BatchPolicy.Select(candidates.Select(c=>c with{Renderer=false}),99).Length==0,"unknown renderer excluded");
        Check(BatchPolicy.Select(candidates.Select(c=>c with{NativeX64=false}),99).Length==0,"WOW64 excluded");
        Check(BatchPolicy.Select(candidates.Select(c=>c with{SameSession=false}),99).Length==0,"other session excluded");
        Check(BatchPolicy.Select(candidates.Select(c=>c with{Family="game"}),99).Length==0,"nonallowlisted family excluded");
        Check(BatchPolicy.Select(candidates,99,"steamwebhelper").Length==0,"refresh does not change family");
        Check(BatchPolicy.Select(new[]{candidates[0],candidates[0]},99).Length==0,"duplicate PID is not a pair");
        Check(BatchPolicy.Conclusion(new[]{"selftests_passed","unsupported_no_measurement"})=="completed_with_skips","unsupported is not measured success");
        Check(BatchPolicy.Conclusion(new[]{"failed_no_valid_result","skipped_no_safe_process_pair"})=="completed_with_failures","failure dominates skip");
        Check(BatchPolicy.Conclusion(new[]{"cancelled_partial_not_result","selftests_passed"})=="cancelled_partial_not_result","cancel partial status");
        Check(BatchPolicy.Conclusion(new[]{"invalid_integrity_not_result"})=="completed_with_failures","integrity failure not pass");
        Check(BatchPolicy.Failure("completed_with_failures"),"auto CLI aggregate failure produces nonzero exit");
        var a=new byte[4096];rnd.NextBytes(a);BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(0,8),0x1010);var b=(byte[])a.Clone();BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0,8),0x5010);
        var snaps=new[]{new ProcessSnapshot(1,1,new[]{new SnapshotRegion(0x1000,4096)},new[]{new SnapshotPage(0x1000,a)}),new ProcessSnapshot(2,2,new[]{new SnapshotRegion(0x5000,4096)},new[]{new SnapshotPage(0x5000,b)})};
        var result=NormalizedDedup.Run(snaps);Check(result.Roundtrip,"normalization roundtrip");Check(result.RawUnique==2&&result.NormalizedUnique==1,"ASLR-like normalization");Check(result.NormalizedDedupBytes>=4096+128,"normalization metadata paid");
        string temp=Path.Combine(Path.GetTempPath(),"ElasticMem_self_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            NormalizedColdStore.Write(Path.Combine(temp,"fixture.emd"),snaps);var restored=NormalizedColdStore.Restore(Path.Combine(temp,"fixture.emd"));Check(restored[0].Pages[0].Bytes.AsSpan().SequenceEqual(a)&&restored[1].Pages[0].Bytes.AsSpan().SequenceEqual(b),"cold container roundtrip");Check(new FileInfo(Path.Combine(temp,"fixture.emd")).Length==result.NormalizedBrotliBytes,"cold footprint exact");
            var repeated=new[]{new ProcessSnapshot(1,1,Array.Empty<SnapshotRegion>(),new[]{new SnapshotPage(0x1000,a),new SnapshotPage(0x2000,a),new SnapshotPage(0x3000,new byte[4096])}),new ProcessSnapshot(2,2,Array.Empty<SnapshotRegion>(),new[]{new SnapshotPage(0x4000,a),new SnapshotPage(0x5000,new byte[4096])})};
            var repeatedMetrics=NormalizedDedup.Run(repeated);var baseline=Path.Combine(temp,"baseline.erb");var raw=Path.Combine(temp,"raw.erb");
            RawColdStore.Write(baseline,repeated,false);RawColdStore.Write(raw,repeated,true);
            Check(new FileInfo(baseline).Length==repeatedMetrics.BrotliOnlyBytes,"brotli only footprint exact");Check(new FileInfo(raw).Length==repeatedMetrics.RawBrotliBytes,"raw dedup footprint exact");Check(new FileInfo(raw).Length<new FileInfo(baseline).Length,"dedup improves repeated-page baseline");
            foreach(var path in new[]{baseline,raw}){var decoded=RawColdStore.Restore(path);for(int p=0;p<repeated.Length;p++)for(int i=0;i<repeated[p].Pages.Length;i++)Check(decoded[p].Pages[i].Address==repeated[p].Pages[i].Address&&decoded[p].Pages[i].Bytes.AsSpan().SequenceEqual(repeated[p].Pages[i].Bytes),"raw roundtrip exact");}
            var rawBlob=File.ReadAllBytes(raw);foreach(var broken in new[]{rawBlob[..^1],rawBlob.Concat(new byte[]{1}).ToArray(),new byte[64]}){File.WriteAllBytes(Path.Combine(temp,"broken.erb"),broken);try{RawColdStore.Restore(Path.Combine(temp,"broken.erb"));Check(false,"raw corruption refused");}catch(Exception ex)when(ex is InvalidDataException or EndOfStreamException){Check(true,"raw corruption refused");}}
            using(var decomposition=JsonDocument.Parse(JsonSerializer.Serialize(RawPageAnalysis.Run(repeated),ResearchRun.Json))){var d=decomposition.RootElement;Check(d.GetProperty("duplicates_within_processes").GetInt32()==1,"within duplicates exact");Check(d.GetProperty("additional_cross_process_copies").GetInt32()==2,"cross duplicates exact");Check(d.GetProperty("zero_duplicate_copies").GetInt32()==1,"zero duplicate exact");}
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
                File.WriteAllText(exportRun.FilePath("inputs/trace.csv"),"private-data");File.WriteAllText(exportRun.FilePath("misplaced-trace.csv"),"private-data");File.WriteAllText(exportRun.FilePath("other/result.json"),"private-data");exportRun.WriteJson("residency-summary-12.json",new{captured_zero_pages=1});File.WriteAllText(exportRun.FilePath("snapshots/12/page-status.csv"),"private-addresses");File.WriteAllText(exportRun.FilePath("brotli-only.erb"),"private-data");exportRun.WriteJson("result.json",new{status="fixture"});exportRun.Seal("passed","unit");
                string zip=Path.Combine(temp,"logs.zip");ResearchRun.ExportLogs(exportRoot,zip,false);
                using(var z=System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    Check(z.Entries.All(e=>!e.FullName.Contains("inputs/")&&!e.FullName.Contains("snapshots/")&&!e.FullName.Contains("other/")&&!e.FullName.EndsWith(".erb")&&!e.FullName.EndsWith("misplaced-trace.csv")),"export privacy allowlist");Check(z.Entries.Any(e=>e.FullName.EndsWith("residency-summary-12.json")),"safe residency aggregate exported");
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
