using System.Buffers.Binary;
using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class ResearchSelfTest
{
    public static object Run()
    {
        int checks=0;var errors=new List<string>();void Check(bool ok,string name){checks++;if(!ok)errors.Add(name);}
        var rnd=new Random(19);var store=new PackedAgentStore(1000,new[]{"a","b"});var shadow=Enumerable.Repeat(new AgentRecord(0,0,-1,0),1000).ToArray();
        foreach(string name in ControlledWorkload.Cases)
        {
            var controlRows=ControlledWorkload.Activity(name);var controlRepeat=ControlledWorkload.Activity(name);
            Check(controlRows.SelectMany(x=>x).SequenceEqual(controlRepeat.SelectMany(x=>x)),"fixed controlled schedule "+name);
            Check(controlRows.All(x=>x.Length==128&&x.Count(v=>v) is 0 or 8),"controlled fixed density "+name);
            string controlPacked=ControlledWorkload.Pack(controlRows);
            Check(ControlledWorkload.Unpack(controlPacked).SelectMany(x=>x).SequenceEqual(controlRows.SelectMany(x=>x)),"controlled bitmap roundtrip "+name);
            bool[][] controlCaseKnown=controlRows.Select(x=>Enumerable.Repeat(true,128).ToArray()).ToArray();
            using var compare=JsonDocument.Parse(JsonSerializer.Serialize(ControlledWorkload.Compare(controlRows,controlRows,controlCaseKnown)));
            Check(compare.RootElement.GetProperty("known_fraction").GetDouble()==1&&compare.RootElement.GetProperty("known_false_negative").GetInt64()==0&&compare.RootElement.GetProperty("known_false_positive").GetInt64()==0,"perfect ground truth observation "+name);
        }
        var controlTruth=ControlledWorkload.Activity("interleaved_correlated");var controlKnown=controlTruth.Select(x=>Enumerable.Repeat(true,128).ToArray()).ToArray();
        var noisy=controlTruth.Select(x=>(bool[])x.Clone()).ToArray();noisy[0][0]=false;noisy[0][1]=true;controlKnown[128][0]=false;
        using(var cmp=JsonDocument.Parse(JsonSerializer.Serialize(ControlledWorkload.Compare(controlTruth,noisy,controlKnown))))
        {Check(cmp.RootElement.GetProperty("known_false_negative").GetInt64()==1,"controlled missing scheduled read");Check(cmp.RootElement.GetProperty("known_false_positive").GetInt64()==1,"controlled unsolicited observed return");Check(cmp.RootElement.GetProperty("unknown_test_epochs").GetInt32()==1,"controlled unknown not false negative");}
        foreach(string broken in new[]{"","%%%","AAAA",ControlledWorkload.Pack(controlTruth)[..^4]})
        {bool refused=false;try{ControlledWorkload.Unpack(broken);}catch(Exception ex)when(ex is InvalidDataException or FormatException){refused=true;}Check(refused,"controlled malformed/truncated bitmap refused");}
        var controlAddresses=Enumerable.Range(1,128).Select(i=>(ulong)i*4096).ToArray();
        var co= CausalGrouping.Simulate(controlAddresses,controlTruth,controlTruth.Select(x=>Enumerable.Repeat(true,128).ToArray()).ToArray(),16,2,"coaccess");
        var adj=CausalGrouping.Simulate(controlAddresses,controlTruth,controlTruth.Select(x=>Enumerable.Repeat(true,128).ToArray()).ToArray(),16,2,"adjacency");
        Check(co.Events.Count>=30&&adj.Events.Count>=30,"constructed control has enough T2cap16 events");
        Check(co.Events.Sum(x=>(long)x.Useful)/(double)co.Events.Count>adj.Events.Sum(x=>(long)x.Useful)/(double)adj.Events.Count,"constructed interleaved positive control tests grouping not renderer benefit");
        var controlPerfect=controlTruth.Select(x=>Enumerable.Repeat(true,128).ToArray()).ToArray();ulong controlChecksum=0;var controlPayload=ControlledWorkload.Contents();
        for(int t=0;t<256;t++)for(int p=0;p<128;p++)if(controlTruth[t][p])controlChecksum=unchecked(controlChecksum+BitConverter.ToUInt64(controlPayload[p],0));
        var controlFixture=JsonSerializer.SerializeToElement(new{name="interleaved_correlated",pages=128,epochs=256,
            truth=ControlledWorkload.Pack(controlTruth),observed=ControlledWorkload.Pack(controlTruth),known=ControlledWorkload.Pack(controlPerfect),
            checksum=controlChecksum,expected_checksum=controlChecksum,timing=Enumerable.Range(0,256).Select(t=>new{epoch=t,scheduled=controlTruth[t].Count(x=>x),retained=0,touch_us=1d,probe_and_touch_us=2d}).ToArray()});
        Check(ControlledWorkload.DecodeCase(controlFixture,"interleaved_correlated").Truth.Length==256,"controlled child fixture independently validated");
        foreach(string bad in new[]{"pages","checksum","truth","known","timing"})
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(controlFixture.GetRawText())!;
            if(bad=="pages")node["pages"]=127;
            if(bad=="checksum")node["checksum"]=0UL;
            if(bad=="truth")node["truth"]=ControlledWorkload.Pack(controlTruth.Select(x=>x.Select(v=>!v).ToArray()).ToArray());
            if(bad=="known")node["known"]=ControlledWorkload.Pack(controlTruth.Select(x=>new bool[128]).ToArray());
            if(bad=="timing")node["timing"]![0]!["retained"]=1;
            bool refused=false;try{ControlledWorkload.DecodeCase(JsonSerializer.SerializeToElement(node),"interleaved_correlated");}catch(InvalidDataException){refused=true;}
            Check(refused,"controlled invalid child "+bad+" refused");
        }
        var lifePlan=LifecycleWorkload.Plan();var lifePayload=ControlledWorkload.Contents();ulong lifeChecksum=0;long lifeCommits=0,lifeDecommits=0;
        for(int t=0;t<256;t++)for(int p=0;p<128;p++)
        {
            if(lifePlan.Touch[t][p])lifeChecksum=unchecked(lifeChecksum+LifecycleWorkload.Word(lifePayload[p],lifePlan.Generation[t][p]));
            if(lifePlan.Alive[t][p]&&(t==0||!lifePlan.Alive[t-1][p]))lifeCommits++;
            if(!lifePlan.Alive[t][p]&&t>0&&lifePlan.Alive[t-1][p])lifeDecommits++;
            Check(!lifePlan.Touch[t][p]||lifePlan.Alive[t][p],"lifecycle touches only live cells");
            if(t>0)Check(lifePlan.Generation[t][p]==lifePlan.Generation[t-1][p]+(lifePlan.Alive[t][p]&&!lifePlan.Alive[t-1][p]?1:0),"lifecycle generation only changes on recommit");
        }
        var lifeFixture=JsonSerializer.SerializeToElement(new{name=LifecycleWorkload.Name,pages=128,epochs=256,alive=ControlledWorkload.Pack(lifePlan.Alive),touch=ControlledWorkload.Pack(lifePlan.Touch),
            before_valid=ControlledWorkload.Pack(Enumerable.Range(0,256).Select(_=>new bool[128]).ToArray()),after_valid=ControlledWorkload.Pack(lifePlan.Touch),generations=Convert.ToBase64String(lifePlan.Generation.SelectMany(x=>x).ToArray()),checksum=lifeChecksum,expected_checksum=lifeChecksum,committed_pages=lifeCommits,decommitted_pages=lifeDecommits});
        using(var lf=JsonDocument.Parse(JsonSerializer.Serialize(LifecycleWorkload.Analyze(lifeFixture))))
        {
            Check(lf.RootElement.GetProperty("active_page_epoch_cells").GetInt64()+lf.RootElement.GetProperty("dead_page_epoch_cells").GetInt64()+lf.RootElement.GetProperty("reserved_never_committed_page_epoch_cells").GetInt64()==32768,"lifecycle disjoint population");
            Check(lf.RootElement.GetProperty("known_false_negative").GetInt64()==0&&lf.RootElement.GetProperty("known_false_positive").GetInt64()==0,"lifecycle perfect live observation");
            Check(lf.RootElement.GetProperty("dead_page_epoch_cells").GetInt64()>0&&lifeCommits>128&&lifeDecommits>128,"lifecycle real transition schedule");
            Check(lf.RootElement.GetProperty("causal_g5_status").GetString()!.StartsWith("skipped"),"lifecycle never silently enters fixed-identity G5");
        }
        foreach(string bad in new[]{"alive","touch","generations","checksum","committed_pages","decommitted_pages","pages"})
        {
            var n=System.Text.Json.Nodes.JsonNode.Parse(lifeFixture.GetRawText())!;
            if(bad is "alive" or "touch")n[bad]=ControlledWorkload.Pack(Enumerable.Range(0,256).Select(_=>new bool[128]).ToArray());
            else if(bad=="generations")n[bad]=Convert.ToBase64String(new byte[32768]);else n[bad]=0;
            bool refused=false;try{LifecycleWorkload.Analyze(JsonSerializer.SerializeToElement(n));}catch(InvalidDataException){refused=true;}Check(refused,"lifecycle corrupt "+bad+" rejected");
        }
        var lifeBefore=Enumerable.Range(0,256).Select(_=>new bool[128]).ToArray();var lifeAfter=lifePlan.Touch.Select(x=>(bool[])x.Clone()).ToArray();
        var lifeNode=System.Text.Json.Nodes.JsonNode.Parse(lifeFixture.GetRawText())!;
        // [0,0] reserved-only, [32,0] truly post-decommit; [0,34] live but unscheduled; [0,33] live and scheduled.
        lifeBefore[0][33]=true;lifeAfter[0][33]=false;lifeAfter[0][0]=true;lifeAfter[32][0]=true;lifeAfter[0][34]=true;
        lifeNode["before_valid"]=ControlledWorkload.Pack(lifeBefore);lifeNode["after_valid"]=ControlledWorkload.Pack(lifeAfter);
        using(var lf=JsonDocument.Parse(JsonSerializer.Serialize(LifecycleWorkload.Analyze(JsonSerializer.SerializeToElement(lifeNode)))))
        {Check(lf.RootElement.GetProperty("retained_active_unknown_cells").GetInt64()==1,"lifecycle retained excluded not FN/dead");Check(lf.RootElement.GetProperty("known_false_negative").GetInt64()==0,"lifecycle unknown scheduled read not counted as missed");Check(lf.RootElement.GetProperty("known_false_positive").GetInt64()==1,"lifecycle live false positive separate");Check(lf.RootElement.GetProperty("dead_reported_valid_after").GetInt64()==1,"lifecycle post-decommit anomaly separate from FP");Check(lf.RootElement.GetProperty("reserved_reported_valid_after").GetInt64()==1,"lifecycle never-committed distinct from death");}
        Check(EpochEligibility.Classify(0,0,true)==EpochExclusion.OriginalMappingUnavailable,"epoch reason original takes precedence");
        Check(EpochEligibility.Classify(1,0,true)==EpochExclusion.CurrentMappingUnavailable,"epoch unavailable mapping not death");
        Check(EpochEligibility.Classify(1,2,true)==EpochExclusion.AllocationChanged,"epoch changed allocation before retained");
        Check(EpochEligibility.Classify(1,1,true)==EpochExclusion.RetainedAfterTrim,"epoch retained not an observed miss");
        Check(EpochEligibility.Classify(1,1,false)==EpochExclusion.Known,"epoch valid known state");
        for(int original=0;original<3;original++)for(int current=0;current<3;current++)foreach(bool retained in new[]{true,false})
            Check((EpochEligibility.Classify((ulong)original,(ulong)current,retained)==EpochExclusion.Known)==(original!=0&&original==current&&!retained),"epoch known semantics unchanged");
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
        var browsers=new[]{new ResearchCandidate(101,"msedge",1,1024,true,true,true),new ResearchCandidate(102,"msedge",2,1024,true,true,true),new ResearchCandidate(103,"chrome",3,999999,true,true,true),new ResearchCandidate(104,"chrome",4,999999,true,true,true),new ResearchCandidate(105,"steamwebhelper",5,9999999,true,true,true),new ResearchCandidate(106,"steamwebhelper",6,9999999,true,true,true)};
        Check(BatchPolicy.SelectBrowser(browsers,999).All(x=>x.Family=="msedge"),"auto browser prefers safe Edge pair over larger Chrome/Steam");
        Check(BatchPolicy.SelectBrowser(browsers.Where(x=>x.Pid!=102),999).All(x=>x.Family=="chrome"),"single Edge does not hide Chrome pair");
        Check(BatchPolicy.SelectBrowser(browsers.Where(x=>x.Family!="msedge"),999,"msedge").Length==0,"explicit Edge never falls back Chrome/Steam");
        Check(BatchPolicy.SelectBrowser(browsers.Where(x=>x.Family=="steamwebhelper"),999).Length==0,"automatic browser never silently selects Steam");
        Check(BatchPolicy.SelectBrowser(browsers,999,"chrome").All(x=>x.Family=="chrome"),"explicit Chrome respected");
        Check(BatchPolicy.SelectBrowser(browsers.Select(x=>x with{Renderer=false}),999).Length==0,"browser role guard retained");
        bool badBrowser=false;try{BatchPolicy.SelectBrowser(browsers,999,"edge");}catch(ArgumentException){badBrowser=true;}Check(badBrowser,"unsupported alias never broadens browser scope");

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
            using(var failedHealth=new ResearchRun(exportRoot,"g0-epochs","unit","fixture",new{}))
            {
                File.WriteAllText(failedHealth.FilePath("epoch-health.csv"),"pid,epoch,known\n12,0,4\n");
                failedHealth.WriteJson("epoch-health-12.json",new{pages=4});
                File.WriteAllText(failedHealth.FilePath("inputs/epoch-health.csv"),"private-raw");
                File.WriteAllText(failedHealth.FilePath("other/epoch-health.csv"),"private-raw");
                File.WriteAllText(failedHealth.FilePath("epoch-health-secret.json"),"private-raw");
                failedHealth.Seal("failed_no_valid_result","synthetic failed source diagnostic");
            }
            using(var exportRun=new ResearchRun(exportRoot,"test","unit","fixture",new{}))
            {
                File.WriteAllText(exportRun.FilePath("inputs/trace.csv"),"private-data");File.WriteAllText(exportRun.FilePath("misplaced-trace.csv"),"private-data");File.WriteAllText(exportRun.FilePath("other/result.json"),"private-data");exportRun.WriteJson("residency-summary-12.json",new{captured_zero_pages=1});File.WriteAllText(exportRun.FilePath("snapshots/12/page-status.csv"),"private-addresses");File.WriteAllText(exportRun.FilePath("brotli-only.erb"),"private-data");exportRun.WriteJson("result.json",new{status="fixture"});exportRun.Seal("passed","unit");
                string zip=Path.Combine(temp,"logs.zip");ResearchRun.ExportLogs(exportRoot,zip,false);
                using(var z=System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    Check(z.Entries.All(e=>!e.FullName.Contains("inputs/")&&!e.FullName.Contains("snapshots/")&&!e.FullName.Contains("other/")&&!e.FullName.EndsWith(".erb")&&!e.FullName.EndsWith("misplaced-trace.csv")),"export privacy allowlist");Check(z.Entries.Any(e=>e.FullName.EndsWith("residency-summary-12.json")),"safe residency aggregate exported");
                    Check(z.Entries.Any(e=>e.FullName.EndsWith("/epoch-health.csv")),"failed run health CSV exported");
                    Check(z.Entries.Any(e=>e.FullName.EndsWith("/epoch-health-12.json")),"numeric health aggregate exported");
                    Check(z.Entries.All(e=>!e.FullName.EndsWith("epoch-health-secret.json")),"arbitrary health suffix excluded");
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
        checks+=NewResearchTests.Run();
        if(errors.Count!=0)throw new InvalidDataException(string.Join("\n",errors));return new{status="selftests_passed",checks,seed=19,native_xpress_fixtures_executed=OperatingSystem.IsWindows(),limitations=new[]{"Core/codec correctness only; native Xpress fixtures only when Windows; no live paging/GPU/UI execution"}};
    }
}
