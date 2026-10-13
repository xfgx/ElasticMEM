using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
namespace ElasticMem.Core.Research;
/// <summary>Actual reversible offline compressed container; never shared as live mapped memory.</summary>
public static class NormalizedColdStore
{
    public static void Write(string path,IReadOnlyList<ProcessSnapshot> snapshots,CancellationToken ct=default)
    {
        if(snapshots.Count<1||snapshots.Count>4||snapshots.Sum(x=>x.Pages.LongLength)>32768)throw new InvalidDataException("Container count cap");
        foreach(var s in snapshots)for(int i=0;i<s.Regions.Length;i++){var r=s.Regions[i];if(s.Regions.Length>65535||r.Size==0||r.Base>ulong.MaxValue-r.Size||i>0&&s.Regions[i-1].Base+s.Regions[i-1].Size>r.Base)throw new InvalidDataException("Invalid region table");}
        var unique=new List<byte[]>();var lookup=new Dictionary<string,List<int>>();var pages=new List<(int Process,int Unique,ulong Address,byte[] Mask)>();
        for(int p=0;p<snapshots.Count;p++)foreach(var page in snapshots[p].Pages)
        {
            ct.ThrowIfCancellationRequested();if(page.Bytes.Length!=4096)throw new InvalidDataException("Page size");var normalized=(byte[])page.Bytes.Clone();var mask=new byte[64];
            for(int w=0;w<512;w++)
            {
                ulong value=BinaryPrimitives.ReadUInt64LittleEndian(normalized.AsSpan(w*8,8));int lo=0,hi=snapshots[p].Regions.Length-1,id=-1;
                while(lo<=hi){int mid=(lo+hi)/2;var region=snapshots[p].Regions[mid];if(value<region.Base)hi=mid-1;else if(value-region.Base>=region.Size)lo=mid+1;else{id=mid;break;}}
                if(id>=0){var r=snapshots[p].Regions[id];if(value-r.Base<=0x0000FFFFFFFFFFFFUL){if(id>65535)throw new InvalidDataException("Region ID");BinaryPrimitives.WriteUInt64LittleEndian(normalized.AsSpan(w*8,8),((ulong)id<<48)|(value-r.Base));mask[w/8]|=(byte)(1<<(w%8));}}
            }
            string hash=Convert.ToHexString(SHA256.HashData(normalized));if(!lookup.TryGetValue(hash,out var bucket)){bucket=new();lookup[hash]=bucket;}int uid=bucket.FirstOrDefault(i=>unique[i].AsSpan().SequenceEqual(normalized),-1);if(uid<0){uid=unique.Count;unique.Add(normalized);bucket.Add(uid);}pages.Add((p,uid,page.Address,mask));
        }
        using var output=File.Create(path);using var b=new BinaryWriter(output);b.Write(new byte[]{69,77,68,49});b.Write(snapshots.Count);b.Write(unique.Count);b.Write(pages.Count);b.Write(new byte[48]);
        foreach(var s in snapshots){b.Write(s.Pid);b.Write(s.StartTicks);b.Write(s.Regions.Length);foreach(var r in s.Regions){b.Write(r.Base);b.Write(r.Size);}}
        foreach(var data in unique){ct.ThrowIfCancellationRequested();using var m=new MemoryStream();using(var z=new BrotliStream(m,CompressionLevel.Fastest,true))z.Write(data);b.Write(checked((int)m.Length));b.Write(m.ToArray());}
        foreach(var p in pages){b.Write(p.Process);b.Write(p.Unique);b.Write(p.Address);b.Write(p.Mask);}
    }
    public static ProcessSnapshot[] Restore(string path)
    {
        if(new FileInfo(path).Length>512L*1024*1024)throw new InvalidDataException("Container size cap");
        using var stream=File.OpenRead(path);using var b=new BinaryReader(stream);if(!b.ReadBytes(4).AsSpan().SequenceEqual(new byte[]{69,77,68,49}))throw new InvalidDataException("Container magic");
        int processCount=b.ReadInt32(),uniqueCount=b.ReadInt32(),pageCount=b.ReadInt32();if(processCount<1||processCount>4||uniqueCount<0||uniqueCount>32768||pageCount<0||pageCount>32768)throw new InvalidDataException("Container count cap");if(b.ReadBytes(48).Length!=48)throw new EndOfStreamException();
        var procs=new (int Pid,long Ticks,SnapshotRegion[] Regions)[processCount];var pages=Enumerable.Range(0,processCount).Select(_=>new List<SnapshotPage>()).ToArray();
        for(int p=0;p<processCount;p++){int pid=b.ReadInt32();long ticks=b.ReadInt64();int n=b.ReadInt32();if(n<0||n>65535)throw new InvalidDataException("Region count");var regions=new SnapshotRegion[n];for(int i=0;i<n;i++){ulong address=b.ReadUInt64(),size=b.ReadUInt64();if(size==0||address>ulong.MaxValue-size||i>0&&regions[i-1].Base+regions[i-1].Size>address)throw new InvalidDataException("Invalid region table");regions[i]=new(address,size);}procs[p]=(pid,ticks,regions);}
        var data=new byte[uniqueCount][];for(int i=0;i<uniqueCount;i++){int n=b.ReadInt32();if(n<1||n>8192)throw new InvalidDataException("Blob size");byte[] blob=b.ReadBytes(n);if(blob.Length!=n)throw new EndOfStreamException();using var m=new MemoryStream(blob);using var z=new BrotliStream(m,CompressionMode.Decompress);data[i]=new byte[4096];z.ReadExactly(data[i]);if(z.ReadByte()!=-1)throw new InvalidDataException("Oversized page");}
        for(int i=0;i<pageCount;i++){int p=b.ReadInt32(),u=b.ReadInt32();ulong address=b.ReadUInt64();byte[] mask=b.ReadBytes(64);if(p<0||p>=processCount||u<0||u>=uniqueCount||mask.Length!=64)throw new InvalidDataException("Page ref");byte[] restored=(byte[])data[u].Clone();for(int w=0;w<512;w++)if((mask[w/8]&(1<<(w%8)))!=0){ulong token=BinaryPrimitives.ReadUInt64LittleEndian(restored.AsSpan(w*8,8));int id=(int)(token>>48);ulong off=token&0x0000FFFFFFFFFFFFUL;if(id>=procs[p].Regions.Length||off>=procs[p].Regions[id].Size)throw new InvalidDataException("Pointer token");BinaryPrimitives.WriteUInt64LittleEndian(restored.AsSpan(w*8,8),procs[p].Regions[id].Base+off);}pages[p].Add(new(address,restored));}
        if(stream.Position!=stream.Length)throw new InvalidDataException("Trailing bytes");return procs.Select((p,i)=>new ProcessSnapshot(p.Pid,p.Ticks,p.Regions,pages[i].ToArray())).ToArray();
    }
}
