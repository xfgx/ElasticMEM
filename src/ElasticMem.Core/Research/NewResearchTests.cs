using System.Buffers.Binary;
using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class NewResearchTests
{
    public static int Run()
    {
        int checks=0;void Check(bool b,string reason){checks++;if(!b)throw new InvalidDataException(reason);}void Reject(Action action,string reason){bool rejected=false;try{action();}catch(Exception ex)when(ex is InvalidDataException or ArgumentException or EndOfStreamException){rejected=true;}Check(rejected,reason);}
        byte[] random=new byte[4096];new Random(73).NextBytes(random);byte[] zero=new byte[4096];byte[] repeated=Enumerable.Repeat((byte)0x5A,4096).ToArray();
        foreach(string name in new[]{"lz4","zstd1"})using(var codec=new PortablePageCodec(name))foreach(var page in new[]{random,zero,repeated})Check(codec.Decode(codec.Encode(page),4096).AsSpan().SequenceEqual(page),name+" fixture");
        if(OperatingSystem.IsWindows())foreach(bool h in new[]{false,true})using(var codec=new WindowsXpressCodec(h))foreach(var (page,fixture) in new[]{(random,"random"),(zero,"zero"),(repeated,"repeat")}){try{Check(codec.Decode(codec.Encode(page),4096).AsSpan().SequenceEqual(page),codec.Name+" native "+fixture+" fixture");}catch(Exception ex){throw new InvalidDataException($"Native fixture failed: codec={codec.Name}, fixture={fixture}, raw_bytes={page.Length}",ex);}}
        using var model=JsonDocument.Parse(JsonSerializer.Serialize(EpochModel.Run(new[]{new[]{false,false},new[]{true,false},new[]{false,false},new[]{true,false}},1,new[]{100d},10000)));
        var first=model.RootElement.GetProperty("curves")[0];Check(first.GetProperty("modeled_reacquisitions_per_second").GetDouble()==.5,"epoch fault recurrence");Check(first.GetProperty("average_end_epoch_offloaded_bytes").GetDouble()==6144,"epoch average end occupancy");Reject(()=>EpochModel.Run(new[]{new[]{true},new[]{true,false}},1,new[]{1d},10),"epoch shape");Reject(()=>EpochModel.Run(new[]{new[]{true},new[]{false}},0,new[]{1d},10),"zero epoch");Check(CodecBench.Quantile(new[]{1d,2,3,4},.5)==2.5,"timing percentile");
        using(var timed=JsonDocument.Parse(JsonSerializer.Serialize(EpochModel.Run(new[]{new[]{false},new[]{true}},1,new[]{100d},10000,new[]{2d,2d}))))Check(timed.RootElement.GetProperty("curves")[0].GetProperty("modeled_reacquisitions_per_second").GetDouble()==.25,"measured epoch duration used");
        Reject(()=>EpochModel.Run(new[]{new[]{true},new[]{false}},1,new[]{1d},double.NaN),"unknown budget rejected");
        Reject(()=>EpochModel.Run(new[]{new[]{true},new[]{false}},1,new[]{1d},10,new[]{1d}),"duration shape rejected");
        using(var dictCodec=new PortablePageCodec("zstd1",repeated))Check(dictCodec.Decode(dictCodec.Encode(repeated),4096).AsSpan().SequenceEqual(repeated),"dictionary decode roundtrip");
        string root=Path.Combine(Path.GetTempPath(),"ElasticMem_newtests_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var trainPages=Enumerable.Range(0,128).Select(i=>{var b=(byte[])repeated.Clone();BinaryPrimitives.WriteInt32LittleEndian(b,i);return b;}).ToArray();var inputs=new[]{new CodecInput(10,1,Enumerable.Range(0,128).Select(i=>(ulong)i*4096).ToArray(),trainPages),new CodecInput(11,2,new ulong[]{0,4096,8192,12288},new[]{random,zero,repeated,(byte[])random.Clone()})};
            var result=CodecBench.Run(inputs,root,null,CancellationToken.None);using var doc=JsonDocument.Parse(JsonSerializer.Serialize(result,ResearchRun.Json));Check(doc.RootElement.GetProperty("training_pid").GetInt32()==10,"training process heldout");Check(doc.RootElement.GetProperty("test_pids")[0].GetInt32()==11,"test process separated");
            foreach(var row in doc.RootElement.GetProperty("codecs").EnumerateArray())if(row.GetProperty("status").GetString()=="serialized_roundtrip_checked")Check(row.GetProperty("total_with_reserve_bytes").GetInt64()>=row.GetProperty("serialized_bytes").GetInt64(),"all layout charges");
            using var codec=new PortablePageCodec("lz4");string file=Path.Combine(root,"lz4-4096.ecb");byte[] bytes=File.ReadAllBytes(file);File.WriteAllBytes(file,bytes[..^1]);Reject(()=>CodecBench.VerifyStore(file,codec,inputs[1].Pages,CancellationToken.None),"truncated container");File.WriteAllBytes(file,bytes.Concat(new byte[]{0}).ToArray());Reject(()=>CodecBench.VerifyStore(file,codec,inputs[1].Pages,CancellationToken.None),"trailing container");File.WriteAllBytes(file,bytes);Reject(()=>CodecBench.VerifyStore(file,codec,new[]{zero,zero,repeated,random},CancellationToken.None),"wrong restore expected");Reject(()=>CodecBench.VerifyStore(file,codec,inputs[1].Pages,CancellationToken.None,new[]{10,10,10,10}),"wrong PID mapping");Reject(()=>CodecBench.VerifyStore(file,codec,inputs[1].Pages,CancellationToken.None,new[]{11,11,11,11},new ulong[]{1,4096,8192,12288}),"wrong address mapping");Reject(()=>CodecBench.Run(new[]{inputs[0],inputs[0]},root,null,CancellationToken.None),"train test leakage rejected");
            byte[] corrupt=(byte[])bytes.Clone();corrupt[0]^=0x5A;File.WriteAllBytes(file,corrupt);Reject(()=>CodecBench.VerifyStore(file,codec,inputs[1].Pages,CancellationToken.None),"corrupt magic rejected");
            using(var pending=new CancellationTokenSource()){pending.Cancel();bool stopped=false;try{CodecBench.Run(inputs,root,null,pending.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancelled codec run rejected");}
        }
        finally{Directory.Delete(root,true);}
        return checks;
    }
}
