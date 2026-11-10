using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using K4os.Compression.LZ4;
using ZstdSharp;
namespace ElasticMem.Core.Research;
public sealed record CodecInput(int Pid,long StartTicks,ulong[] Addresses,byte[][] Pages);
public interface IPageCodec:IDisposable { string Name{get;} byte[] Encode(byte[] src); byte[] Decode(byte[] src,int length); }
public sealed class PortablePageCodec:IPageCodec
{
    readonly string _kind; readonly Compressor? _c;readonly Decompressor? _d; readonly System.IO.Compression.CompressionLevel _deflateLevel;
    public string Name{get;}
    // kind: lz4 | zstd1 | deflate (Optimal) | deflate-fast (Fastest) | deflate-max (SmallestSize)
    public PortablePageCodec(string kind,byte[]? dictionary=null)
    {
        _kind=kind;Name=kind+(dictionary!=null?"-dictionary":"");
        if(kind=="zstd1"){_c=new(1);_d=new();if(dictionary!=null){_c.LoadDictionary(dictionary);_d.LoadDictionary(dictionary);}}
        else if(kind=="deflate")_deflateLevel=System.IO.Compression.CompressionLevel.Optimal;
        else if(kind=="deflate-fast")_deflateLevel=System.IO.Compression.CompressionLevel.Fastest;
        else if(kind=="deflate-max")_deflateLevel=System.IO.Compression.CompressionLevel.SmallestSize;
        else if(kind!="lz4")throw new ArgumentException("codec");
    }
    bool IsDeflate=>_kind.StartsWith("deflate",StringComparison.Ordinal);
    public byte[] Encode(byte[] b)
    {
        if(_c!=null)return _c.Wrap(b).ToArray();
        if(IsDeflate){using var ms=new MemoryStream(b.Length);using(var ds=new System.IO.Compression.DeflateStream(ms,_deflateLevel,true))ds.Write(b,0,b.Length);return ms.ToArray();}
        var d=new byte[LZ4Codec.MaximumOutputSize(b.Length)];int n=LZ4Codec.Encode(b,0,b.Length,d,0,d.Length);if(n<=0)throw new InvalidDataException("LZ4 encode");return d[..n];
    }
    public byte[] Decode(byte[] b,int length)
    {
        if(_d!=null){var d=_d.Unwrap(b,length).ToArray();if(d.Length!=length)throw new InvalidDataException("zstd length");return d;}
        if(IsDeflate)
        {
            var x=new byte[length];using var ms=new MemoryStream(b,false);using var ds=new System.IO.Compression.DeflateStream(ms,System.IO.Compression.CompressionMode.Decompress);
            int read=0;while(read<length){int n=ds.Read(x,read,length-read);if(n==0)break;read+=n;}
            if(read!=length||ds.ReadByte()!=-1)throw new InvalidDataException("deflate length");return x;
        }
        var y=new byte[length];if(LZ4Codec.Decode(b,0,b.Length,y,0,length)!=length)throw new InvalidDataException("LZ4 length");return y;
    }
    public void Dispose(){_c?.Dispose();_d?.Dispose();}
}
public sealed class WindowsXpressCodec:IPageCodec
{
    [DllImport("ntdll.dll")]static extern int RtlGetCompressionWorkSpaceSize(ushort format,out uint workspace,out uint fragment);
    [DllImport("ntdll.dll")]static extern int RtlCompressBuffer(ushort format,byte[] source,uint length,byte[] dest,uint capacity,uint chunk,out uint size,IntPtr work);
    [DllImport("ntdll.dll")]static extern int RtlDecompressBuffer(ushort format,[Out]byte[] dest,uint capacity,byte[] source,uint length,out uint size);
    [DllImport("ntdll.dll")]static extern int RtlDecompressBufferEx(ushort format,[Out]byte[] dest,uint capacity,byte[] source,uint length,out uint size,IntPtr workspace);
    readonly ushort _format;IntPtr _workspace,_decodeWorkspace;public string Name=>_format==3?"xpress":"xpress-huffman";
    public WindowsXpressCodec(bool huffman){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows Xpress API");_format=(ushort)(huffman?4:3);int status=RtlGetCompressionWorkSpaceSize(_format,out uint n,out uint fragment);if(status<0)throw new IOException("Xpress workspace NTSTATUS "+status.ToString("X8"));if(n>16<<20||fragment>16<<20)throw new IOException("Workspace cap");_workspace=Marshal.AllocHGlobal(Math.Max(1,(int)n));try{if(huffman)_decodeWorkspace=Marshal.AllocHGlobal(Math.Max(1,(int)Math.Max(n,fragment)));}catch{Marshal.FreeHGlobal(_workspace);_workspace=IntPtr.Zero;throw;}}
    public byte[] Encode(byte[] b){if(_workspace==IntPtr.Zero)throw new ObjectDisposedException(nameof(WindowsXpressCodec));var d=new byte[b.Length*2+4096];int status=RtlCompressBuffer(_format,b,(uint)b.Length,d,(uint)d.Length,4096,out uint n,_workspace);if(status==0x117)return new byte[]{1};if(status<0)throw new IOException("Xpress compression NTSTATUS "+status.ToString("X8"));var framed=new byte[n+1];d.AsSpan(0,(int)n).CopyTo(framed.AsSpan(1));return framed;}
    public byte[] Decode(byte[] b,int length){if(_workspace==IntPtr.Zero)throw new ObjectDisposedException(nameof(WindowsXpressCodec));if(b.Length==1&&b[0]==1)return new byte[length];if(b.Length<2||b[0]!=0)throw new InvalidDataException("Xpress frame");var d=new byte[length];byte[] native=b[1..];uint n;int status=_format==4?RtlDecompressBufferEx(_format,d,(uint)d.Length,native,(uint)native.Length,out n,_decodeWorkspace):RtlDecompressBuffer(_format,d,(uint)d.Length,native,(uint)native.Length,out n);if(status<0||n!=length)throw new InvalidDataException($"{Name} decompression API={(_format==4?"RtlDecompressBufferEx":"RtlDecompressBuffer")} NTSTATUS={status:X8}, compressed={native.Length}, expected={length}, actual={n}");return d;}
    public void Dispose(){if(_workspace!=IntPtr.Zero){Marshal.FreeHGlobal(_workspace);_workspace=IntPtr.Zero;}if(_decodeWorkspace!=IntPtr.Zero){Marshal.FreeHGlobal(_decodeWorkspace);_decodeWorkspace=IntPtr.Zero;}}
}
public static class CodecBench
{
    public static double Quantile(IEnumerable<double> v,double q){var a=v.Order().ToArray();if(a.Length==0)throw new ArgumentException("Empty timing");double x=(a.Length-1)*q;int lo=(int)x,hi=(int)Math.Ceiling(x);return a[lo]+(a[hi]-a[lo])*(x-lo);}
    static double Us(long t)=>(Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency;
    static int Align8(int n)=>(n+7)&~7;
    public static object Run(CodecInput[] input,string output,Action<string>? progress,CancellationToken ct,bool heldOutTraining=true)
    {
        if(!heldOutTraining && input.Length!=1)throw new ArgumentException("Untrained application comparison requires exactly one process");
        if((heldOutTraining && input.Length<2)||input.Select(x=>(x.Pid,x.StartTicks)).Distinct().Count()!=input.Length)throw new ArgumentException("Need distinct training and held-out processes");
        if(input.Any(x=>x.Pages.Length!=x.Addresses.Length||x.Pages.Any(p=>p.Length!=4096)))throw new ArgumentException("Page shape");
        var train=input[0];var test=input.Skip(heldOutTraining?1:0).SelectMany(x=>x.Pages.Take(2048).Select((p,i)=>(x.Pid,Address:x.Addresses[i],Bytes:p))).ToArray();if(test.Length<4)throw new ArgumentException("Too few held-out pages");Directory.CreateDirectory(output);
        byte[]? dict=null;string? trainingError=null;try{ct.ThrowIfCancellationRequested();if(heldOutTraining)dict=DictBuilder.TrainFromBuffer(train.Pages.Take(1024),65536);}catch(Exception ex)when(ex is not OperationCanceledException){trainingError=ex.GetType().Name+": "+ex.Message;}
        if(dict!=null)File.WriteAllBytes(Path.Combine(output,"zstd-training-dictionary.bin"),dict);
        var results=new List<object>();var totals=new Dictionary<string,long>();var rawRows=new List<string>{"codec,block_bytes,block_index,pages,compress_us,codec_decompress_us,stored_restore_us,stored_payload_bytes,raw_fallback"};
        foreach(var (kind,blockBytes,useDict) in new[]{("xpress",4096,false),("xpress-huffman",4096,false),("lz4",4096,false),("zstd1",4096,false),("zstd1",4096,true),("zstd1",16384,false)})
        {
            ct.ThrowIfCancellationRequested();string label=kind+(useDict?"-dictionary":"")+"-"+blockBytes;progress?.Invoke(label);
            if(useDict&&!heldOutTraining)continue;
            if(useDict&&dict==null){results.Add(new{codec=label,status="skipped_dictionary_training_failed",error=trainingError});continue;}
            if(kind.StartsWith("xpress")&&!OperatingSystem.IsWindows()){results.Add(new{codec=label,status="unsupported_windows_api"});continue;}
            using IPageCodec codec=kind.StartsWith("xpress")?new WindowsXpressCodec(kind=="xpress-huffman"):new PortablePageCodec(kind,useDict?dict:null);
            var enc=new List<double>();var dec=new List<double>();var storedRestore=new List<double>();int group=blockBytes/4096;var blocks=new List<(int Start,int Pages,byte[] Raw,byte[] Stored,bool Fallback)>();
            // Warmup is excluded; timings include managed allocations, exclude equality check and store I/O.
            for(int w=0;w<16;w++){var b=codec.Encode(test[w%test.Length].Bytes);if(!codec.Decode(b,4096).AsSpan().SequenceEqual(test[w%test.Length].Bytes))throw new InvalidDataException("Warmup restore mismatch");}
            for(int i=0;i<test.Length;i+=group)
            {
                ct.ThrowIfCancellationRequested();int pages=Math.Min(group,test.Length-i);var raw=new byte[pages*4096];for(int p=0;p<pages;p++)test[i+p].Bytes.CopyTo(raw,p*4096);
                long t=Stopwatch.GetTimestamp();byte[] packed=codec.Encode(raw);enc.Add(Us(t));t=Stopwatch.GetTimestamp();byte[] unpacked=codec.Decode(packed,raw.Length);dec.Add(Us(t));if(!unpacked.AsSpan().SequenceEqual(raw))throw new InvalidDataException(label+" byte mismatch");
                bool fallback=packed.Length>=raw.Length;byte[] stored=fallback?raw:packed;t=Stopwatch.GetTimestamp();byte[] actualStoredRestore=fallback?(byte[])stored.Clone():codec.Decode(stored,raw.Length);storedRestore.Add(Us(t));if(!actualStoredRestore.AsSpan().SequenceEqual(raw))throw new InvalidDataException("Stored representation restore mismatch");blocks.Add((i,pages,raw,stored,fallback));rawRows.Add(FormattableString.Invariant($"{label},{blockBytes},{blocks.Count-1},{pages},{enc[^1]:F6},{dec[^1]:F6},{storedRestore[^1]:F6},{stored.Length},{(fallback?1:0)}"));
            }
            string path=Path.Combine(output,label+".ecb");using(var f=File.Create(path))using(var w=new BinaryWriter(f))
            {
                w.Write(0x31424345);w.Write(codec.Name);w.Write(blockBytes);w.Write(test.Length);w.Write(blocks.Count);byte[] dictionary=useDict?dict!:Array.Empty<byte>();w.Write(dictionary.Length);w.Write(dictionary);
                for(int i=0;i<test.Length;i++){w.Write(test[i].Pid);w.Write(0);w.Write(test[i].Address);w.Write(i/group);w.Write((i%group)*4096);}
                foreach(var b in blocks){w.Write(b.Raw.Length);w.Write(b.Stored.Length);w.Write(b.Fallback);w.Write(new byte[7]);w.Write(b.Stored);w.Write(new byte[Align8(b.Stored.Length)-b.Stored.Length]);}
            }
            VerifyStore(path,codec,test.Select(x=>x.Bytes).ToArray(),ct,test.Select(x=>x.Pid).ToArray(),test.Select(x=>x.Address).ToArray());long serialized=new FileInfo(path).Length;long allocated=((serialized+255)/256)*256;long reserve=((allocated*8+99)/100+255)/256*256;long total=allocated+reserve;totals[label]=total;
            results.Add(new{codec=label,status="serialized_roundtrip_checked",block_bytes=blockBytes,test_pages=test.Length,input_bytes=(long)test.Length*4096,serialized_bytes=serialized,allocation_chunk_bytes=256,allocated_bytes=allocated,reserve_fraction=.08,reserve_bytes=reserve,total_with_reserve_bytes=total,ratio_raw_to_store=(double)test.Length*4096/total,dictionary_bytes=useDict?dict!.Length:0,raw_fallback_blocks=blocks.Count(b=>b.Fallback),compress_p50_us_per_block=Quantile(enc,.5),compress_p95_us_per_block=Quantile(enc,.95),codec_decompress_p50_us_per_block=Quantile(dec,.5),codec_decompress_p95_us_per_block=Quantile(dec,.95),mean_compress_us_per_page=enc.Sum()/test.Length,mean_codec_decompress_us_per_page=dec.Sum()/test.Length,stored_payload_restore_p50_us_per_block=Quantile(storedRestore,.5),stored_payload_restore_p95_us_per_block=Quantile(storedRestore,.95),mean_stored_payload_restore_us_per_page=storedRestore.Sum()/test.Length,sha256=ResearchRun.Hash(path)});
        }
        // Raw codec microtimings exportable; container and dictionary data stay sensitive.
        File.WriteAllLines(Path.Combine(output,"codec-samples.csv"),rawRows);
        long? baseline=totals.TryGetValue("xpress-4096",out long xpress)?xpress:null;
        return new{schema="elasticmem.g4.codecs.v1",status="offline_codec_comparison_not_live_reclaim",comparison_kind=heldOutTraining?"held_out_process_with_dictionary":"single_process_fixed_codecs_no_training",training_pid=heldOutTraining?(int?)train.Pid:null,training_start_ticks=heldOutTraining?(long?)train.StartTicks:null,training_pages=heldOutTraining?Math.Min(train.Pages.Length,1024):0,dictionary_training_error=trainingError,test_pids=input.Skip(heldOutTraining?1:0).Select(x=>x.Pid).ToArray(),test_pages=test.Length,codecs=results,gate=totals.Where(x=>x.Key.StartsWith("zstd")||x.Key.StartsWith("lz4")).Select(x=>new{codec=x.Key,xpress_relative_saving=baseline.HasValue?(double?)(1-(double)x.Value/baseline.Value):null,capacity_25_percent_gate=baseline.HasValue?(bool?)(x.Value<=baseline.Value*.75):null}),limitations=new[]{"Windows Xpress API is a codec reference, not a replica of Windows compressed-store layout","Fixed 24-byte page bindings,16-byte block headers,8-byte padding,256-byte allocation and8% reserve counted; runtime workspace/RSS not counted","Xpress1-byte framing counts native zero status explicitly; zero pages are never free",heldOutTraining?"Single bounded address-prefix capture, training PID excluded from test; no random/population coverage guarantee":"Single process bounded address-prefix capture; fixed codecs, no dictionary/training, no random/population coverage guarantee","Stored-payload latency follows actual raw fallback or codec decode; excludes container parsing and I/O","Fresh compressor per codec;16 warmups,one timed pass,managed allocation cost included; timings not disk/pagefile path","16KiB decoder restores whole blocks; mean per-page time is amortized,not random4KiB latency","Capacity gate alone is not a shipping decision; no kernel driver,pagefile changes,automatic upload or physical RAM reclaim"}};
    }
    public static void VerifyStore(string path,IPageCodec codec,byte[][] expected,CancellationToken ct,int[]? expectedPids=null,ulong[]? expectedAddresses=null)
    {
        using var f=File.OpenRead(path);using var r=new BinaryReader(f);if(r.ReadInt32()!=0x31424345||r.ReadString()!=codec.Name)throw new InvalidDataException("Container header");int block=r.ReadInt32(),pages=r.ReadInt32(),blocks=r.ReadInt32(),dict=r.ReadInt32();if(block is not(4096 or 16384)||pages!=expected.Length||blocks!=(pages+(block/4096)-1)/(block/4096)||dict<0||dict>112640)throw new InvalidDataException("Container dimensions");var dictionary=r.ReadBytes(dict);if(dictionary.Length!=dict)throw new EndOfStreamException();
        using IPageCodec fresh=codec.Name.StartsWith("xpress")?new WindowsXpressCodec(codec.Name=="xpress-huffman"):new PortablePageCodec(codec.Name.StartsWith("zstd1")?"zstd1":"lz4",codec.Name.EndsWith("-dictionary")?dictionary:null);if((codec.Name.EndsWith("-dictionary")&&dict==0)||(!codec.Name.EndsWith("-dictionary")&&dict!=0))throw new InvalidDataException("Dictionary/header mismatch");
        for(int i=0;i<pages;i++){int pid=r.ReadInt32();if(expectedPids!=null&&pid!=expectedPids[i])throw new InvalidDataException("PID binding");if(r.ReadInt32()!=0)throw new InvalidDataException("page padding");ulong address=r.ReadUInt64();if(expectedAddresses!=null&&address!=expectedAddresses[i])throw new InvalidDataException("Address binding");if(r.ReadInt32()!=i/(block/4096)||r.ReadInt32()!=i%(block/4096)*4096)throw new InvalidDataException("Page binding");}
        int at=0;for(int i=0;i<blocks;i++){ct.ThrowIfCancellationRequested();int n=r.ReadInt32(),stored=r.ReadInt32();bool raw=r.ReadBoolean();var padding=r.ReadBytes(7);if(n!=Math.Min(block/4096,pages-at)*4096||n<=0||n>block||n%4096!=0||stored<=0||stored>block*2+4096||padding.Length!=7||padding.Any(x=>x!=0))throw new InvalidDataException("Block dimensions");byte[] data=r.ReadBytes(stored);if(data.Length!=stored)throw new EndOfStreamException();var tail=r.ReadBytes(Align8(stored)-stored);if(tail.Length!=Align8(stored)-stored||tail.Any(x=>x!=0))throw new InvalidDataException("Block padding");byte[] b=raw?data:fresh.Decode(data,n);if(b.Length!=n)throw new InvalidDataException("Raw fallback length");for(int p=0;p<n/4096;p++){if(at>=expected.Length||!b.AsSpan(p*4096,4096).SequenceEqual(expected[at++]))throw new InvalidDataException("Serialized restore mismatch");}}
        if(at!=expected.Length||f.Position!=f.Length)throw new InvalidDataException("Container trailing/truncated");
    }
}
