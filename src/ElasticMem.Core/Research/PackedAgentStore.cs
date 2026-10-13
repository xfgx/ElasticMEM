using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
namespace ElasticMem.Core.Research;
public readonly record struct AgentRecord(ushort TypeId,uint References,int Next,long Value);
/// <summary>Real compact store for OWN schema-controlled records; not an external runtime patch.</summary>
public sealed class PackedAgentStore
{
    private readonly byte[] _data;private readonly Dictionary<int,uint> _extended=new();
    public string[] Types {get;} public int Count {get;}
    public long SerializedBytes => _data.LongLength+_extended.Count*8L+Types.Sum(t=>Encoding.UTF8.GetByteCount(t)+4L)+16;
    public PackedAgentStore(int count,string[] types){if(count<0||count>5_000_000||types.Length>65535||types.Length==0)throw new ArgumentOutOfRangeException();Count=count;Types=(string[])types.Clone();_data=new byte[checked(count*16)];for(int i=0;i<count;i++)BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(i*16+4,4),-1);}
    public void Set(int index,AgentRecord value)
    {
        if((uint)index>=(uint)Count||value.TypeId>=Types.Length||value.Next < -1||value.Next>=Count)throw new ArgumentOutOfRangeException();
        var s=_data.AsSpan(index*16,16);BinaryPrimitives.WriteUInt16LittleEndian(s,value.TypeId);
        if(value.References>=ushort.MaxValue){BinaryPrimitives.WriteUInt16LittleEndian(s[2..],ushort.MaxValue);_extended[index]=value.References;}
        else{BinaryPrimitives.WriteUInt16LittleEndian(s[2..],(ushort)value.References);_extended.Remove(index);}
        BinaryPrimitives.WriteInt32LittleEndian(s[4..],value.Next);BinaryPrimitives.WriteInt64LittleEndian(s[8..],value.Value);
    }
    public AgentRecord Get(int index){if((uint)index>=(uint)Count)throw new ArgumentOutOfRangeException();var s=_data.AsSpan(index*16,16);ushort refs=BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);return new(BinaryPrimitives.ReadUInt16LittleEndian(s),refs==ushort.MaxValue?_extended[index]:refs,BinaryPrimitives.ReadInt32LittleEndian(s[4..]),BinaryPrimitives.ReadInt64LittleEndian(s[8..]));}
    public static object Benchmark(int count,CancellationToken ct=default)
    {
        if(count<1||count>5_000_000)throw new ArgumentOutOfRangeException(nameof(count));
        string[] types={"task","message","tool_call","result","event","memory","plan","edge"};var store=new PackedAgentStore(count,types);var shadow=new AgentRecord[count];var random=new Random(41);
        var sw=Stopwatch.StartNew();for(int i=0;i<count;i++){if(i%4096==0)ct.ThrowIfCancellationRequested();var r=new AgentRecord((ushort)(i%types.Length),i%997==0?uint.MaxValue:(uint)random.Next(60000),i+1==count?-1:i+1,random.NextInt64());shadow[i]=r;store.Set(i,r);}double buildMs=sw.Elapsed.TotalMilliseconds;
        sw.Restart();long checksum=0;for(int i=0;i<count;i++){if(i%4096==0)ct.ThrowIfCancellationRequested();var r=store.Get(i);if(r!=shadow[i])throw new InvalidDataException("Typed roundtrip mismatch");checksum^=r.Value;}double readMs=sw.Elapsed.TotalMilliseconds;
        for(int i=0;i<Math.Min(count,10000);i++){var r=shadow[i] with{References=(uint)(i%2==0?65535:1)};store.Set(i,r);if(store.Get(i)!=r)throw new InvalidDataException("Typed update mismatch");shadow[i]=r;}
        return new{schema="elasticmem.g2.v1",status="own_schema_benchmark",records=count,baseline_serialized_bytes=count*24L+types.Sum(t=>Encoding.UTF8.GetByteCount(t)+4L)+16,packed_serialized_bytes=store.SerializedBytes,build_ms=buildMs,verified_read_ms=readMs,checksum,roundtrip=true,
            limitations=new[]{"Fixed schema controlled by ElasticMem; not CPython/V8 heap compression","24-byte canonical baseline, not actual managed object RSS","32-bit index handles and ushort type IDs; large refcounts pay 8-byte escapes","GC/dictionary/allocator overhead not represented by serialized-byte metric","Timing includes correctness verification; not an uninstrumented access benchmark"}};
    }
}
