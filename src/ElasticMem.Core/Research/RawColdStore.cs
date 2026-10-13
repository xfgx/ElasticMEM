using System.IO.Compression;
using System.Security.Cryptography;
namespace ElasticMem.Core.Research;
/// <summary>Actual page-wise Brotli baseline, optionally deduplicated; all headers/refs/prefixes serialized.</summary>
public static class RawColdStore
{
    static readonly byte[] Magic = {69,82,66,49};
    public static void Write(string path,IReadOnlyList<ProcessSnapshot> snapshots,bool deduplicate,CancellationToken ct=default)
    {
        int count=snapshots.Sum(s=>s.Pages.Length);if(snapshots.Count is <1 or >4||count is <1 or >32768)throw new InvalidDataException("Raw count cap");
        var blocks=new List<byte[]>();var refs=new List<(int P,int U,ulong A)>();var hashes=new Dictionary<string,List<int>>();
        for(int p=0;p<snapshots.Count;p++)foreach(var page in snapshots[p].Pages)
        {
            ct.ThrowIfCancellationRequested();if(page.Bytes.Length!=4096||page.Address%4096!=0)throw new InvalidDataException("Raw page");int uid=-1;
            if(deduplicate){string key=Convert.ToHexString(SHA256.HashData(page.Bytes));if(!hashes.TryGetValue(key,out var bucket)){bucket=new();hashes[key]=bucket;}uid=bucket.FirstOrDefault(i=>blocks[i].AsSpan().SequenceEqual(page.Bytes),-1);if(uid<0){uid=blocks.Count;blocks.Add(page.Bytes);bucket.Add(uid);}}
            else{uid=blocks.Count;blocks.Add(page.Bytes);}refs.Add((p,uid,page.Address));
        }
        using var b=new BinaryWriter(File.Create(path));b.Write(Magic);b.Write(snapshots.Count);b.Write(blocks.Count);b.Write(count);b.Write(deduplicate?1:0);b.Write(new byte[44]);
        foreach(var s in snapshots){b.Write(s.Pid);b.Write(s.StartTicks);b.Write(0);}
        foreach(var page in blocks){ct.ThrowIfCancellationRequested();using var m=new MemoryStream();using(var brotli=new BrotliStream(m,CompressionLevel.Fastest,true))brotli.Write(page);b.Write(checked((int)m.Length));b.Write(m.ToArray());}
        foreach(var r in refs){b.Write(r.P);b.Write(r.U);b.Write(r.A);}
    }
    public static ProcessSnapshot[] Restore(string path)
    {
        if(new FileInfo(path).Length>512L<<20)throw new InvalidDataException("Raw size cap");using var b=new BinaryReader(File.OpenRead(path));
        if(!b.ReadBytes(4).AsSpan().SequenceEqual(Magic))throw new InvalidDataException("Raw magic");int np=b.ReadInt32(),nu=b.ReadInt32(),n=b.ReadInt32(),mode=b.ReadInt32();
        if(np is <1 or >4||nu is <1 or >32768||n is <1 or >32768||nu>n||mode is <0 or >1||mode==0&&nu!=n)throw new InvalidDataException("Raw count cap");if(b.ReadBytes(44).Length!=44)throw new EndOfStreamException();
        var procs=new (int Pid,long Ticks)[np];var pages=Enumerable.Range(0,np).Select(_=>new List<SnapshotPage>()).ToArray();
        for(int i=0;i<np;i++){procs[i]=(b.ReadInt32(),b.ReadInt64());if(b.ReadInt32()!=0)throw new InvalidDataException("Raw process reserved");}
        var blocks=new byte[nu][];
        for(int i=0;i<nu;i++){int length=b.ReadInt32();if(length is <1 or >8192)throw new InvalidDataException("Raw blob size");byte[] data=b.ReadBytes(length);if(data.Length!=length)throw new EndOfStreamException();using var m=new MemoryStream(data);using var z=new BrotliStream(m,CompressionMode.Decompress);blocks[i]=new byte[4096];z.ReadExactly(blocks[i]);if(z.ReadByte()!=-1)throw new InvalidDataException("Raw oversized page");}
        var seen=new HashSet<(int,ulong)>();var used=new HashSet<int>();
        for(int i=0;i<n;i++){int p=b.ReadInt32(),u=b.ReadInt32();ulong a=b.ReadUInt64();if(p<0||p>=np||u<0||u>=nu||a%4096!=0||!seen.Add((p,a)))throw new InvalidDataException("Raw ref");if(mode==0&&!used.Add(u))throw new InvalidDataException("Raw non-dedup ref reuse");pages[p].Add(new(a,(byte[])blocks[u].Clone()));}
        if(b.BaseStream.Position!=b.BaseStream.Length)throw new InvalidDataException("Raw trailing bytes");return procs.Select((p,i)=>new ProcessSnapshot(p.Pid,p.Ticks,Array.Empty<SnapshotRegion>(),pages[i].ToArray())).ToArray();
    }
}
