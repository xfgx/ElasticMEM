namespace ElasticMem.Core.Research;
public static class EpochTraceFile
{
    public static void Write(string path,int pid,long start,ulong[] addresses,bool[] known,bool[][] activity,bool[][]? knownRows=null,bool[]? initialResident=null)
    {
        if(addresses.Length!=known.Length||activity.Any(a=>a.Length!=addresses.Length))throw new ArgumentException("Epoch trace shape");
        using var stream=File.Create(path);using var w=new BinaryWriter(stream);
        knownRows??=activity.Select(a=>(bool[])known.Clone()).ToArray();
        if(knownRows.Length!=activity.Length||knownRows.Any(a=>a.Length!=addresses.Length))throw new ArgumentException("Epoch known shape");
        if(initialResident!=null&&initialResident.Length!=addresses.Length)throw new ArgumentException("Initial residency shape");
        w.Write(initialResident==null?0x32545045:0x33545045);w.Write(pid);w.Write(start);w.Write(addresses.Length);w.Write(activity.Length);
        foreach(ulong address in addresses)w.Write(address);
        void Bits(bool[] row){var b=new byte[(row.Length+7)/8];for(int i=0;i<row.Length;i++)if(row[i])b[i/8]|=(byte)(1<<(i%8));w.Write(b);}
        Bits(known);if(initialResident!=null)Bits(initialResident);foreach(var row in activity)Bits(row);foreach(var row in knownRows)Bits(row);
    }
    public static (int Pid,long Start,ulong[] Addresses,bool[] Known,bool[][] Activity,bool[][] KnownRows,bool[]? InitialResident) Read(string path,int? plannedEpochs=null)
    {
        using var stream=File.OpenRead(path);using var r=new BinaryReader(stream);
        int magic=r.ReadInt32();if(magic!=0x32545045&&magic!=0x33545045)throw new InvalidDataException("Epoch magic");
        int pid=r.ReadInt32();long start=r.ReadInt64();int pages=r.ReadInt32(),epochs=r.ReadInt32();
        if(plannedEpochs.HasValue&&epochs!=plannedEpochs.Value)throw new InvalidDataException("Trace does not match full planned epoch count");
        if(pid<=4||start<=0||pages is <1 or >262144||epochs is <8 or >900||stream.Length!=24L+pages*8L+(long)((pages+7)/8)*(epochs*2+(magic==0x33545045?2:1)))throw new InvalidDataException("Epoch trace bounds/length");
        var addresses=Enumerable.Range(0,pages).Select(_=>r.ReadUInt64()).ToArray();
        if(addresses.Distinct().Count()!=pages||addresses.Any(a=>a%4096!=0))throw new InvalidDataException("Epoch duplicate/unaligned address");
        bool[] Bits(){byte[] b=r.ReadBytes((pages+7)/8);if(b.Length!=(pages+7)/8)throw new EndOfStreamException();return Enumerable.Range(0,pages).Select(i=>(b[i/8]&(1<<(i%8)))!=0).ToArray();}
        var known=Bits();bool[]? initial=magic==0x33545045?Bits():null;var activity=Enumerable.Range(0,epochs).Select(_=>Bits()).ToArray();var knownRows=Enumerable.Range(0,epochs).Select(_=>Bits()).ToArray();
        return(pid,start,addresses,known,activity,knownRows,initial);
    }
}