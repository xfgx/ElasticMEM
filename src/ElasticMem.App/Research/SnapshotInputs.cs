using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class SnapshotInputs
{
    public static CodecInput[] Load(string source,ResearchRun run,CancellationToken ct,int epochPid=0)
    {
        if(epochPid<0)throw new ArgumentOutOfRangeException(nameof(epochPid));
        ResearchRun.Verify(source);using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(source,"manifest.json")));
        bool single=m.RootElement.GetProperty("mode").GetString()=="epoch-snapshot"&&m.RootElement.GetProperty("status").GetString()=="fresh_renderer_snapshot_checked";
        if(single&&epochPid<=0)throw new ArgumentException("Single snapshot is only an explicit epoch PID source,not cross-process codec evidence");
        if(!single&&(m.RootElement.GetProperty("mode").GetString()!="g1"||m.RootElement.GetProperty("status").GetString()!="resident_private_offline_roundtrip_checked"))throw new ArgumentException("Successful resident snapshot source required");
        var snapshots=Directory.GetDirectories(Path.Combine(source,"snapshots")).Order().ToArray();if(single?snapshots.Length!=1:snapshots.Length is <2 or >4)throw new ArgumentException("Invalid source process count");var result=new List<CodecInput>();long total=0;string? family=null;
        foreach(var dir in snapshots)
        {
            ct.ThrowIfCancellationRequested();using var inv=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"inventory.json")));var root=inv.RootElement;string name=root.GetProperty("process_name").GetString()!;if(!new[]{"chrome","msedge","steamwebhelper"}.Contains(name,StringComparer.OrdinalIgnoreCase)||family!=null&&!name.Equals(family,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Same-family renderer G1 source required");family=name;
            int pid=root.GetProperty("pid").GetInt32();if(epochPid>0&&pid!=epochPid)continue;long length=new FileInfo(Path.Combine(dir,"pages.bin")).Length;total+=length;if(total>128L<<20||length%4096!=0)throw new ArgumentException("Snapshot size cap/shape");string inventory=run.CopyInput(Path.Combine(dir,"inventory.json"),"inventory-"+pid+".json");string path=run.CopyInput(Path.Combine(dir,"pages.bin"),"pages-"+pid+".bin");byte[] bytes=File.ReadAllBytes(path);var pageItems=root.GetProperty("pages").EnumerateArray().ToArray();if(pageItems.Length*4096!=bytes.Length)throw new InvalidDataException("Page count");var pages=new byte[pageItems.Length][];var addresses=new ulong[pages.Length];for(int i=0;i<pages.Length;i++){if(pageItems[i].GetProperty("file_offset").GetInt64()!=i*4096L)throw new InvalidDataException("Page offset");addresses[i]=pageItems[i].GetProperty("address").GetUInt64();pages[i]=bytes.AsSpan(i*4096,4096).ToArray();}result.Add(new(pid,root.GetProperty("start_ticks").GetInt64(),addresses,pages));
        }
        if(epochPid>0&&(result.Count!=1||result[0].Pid!=epochPid))throw new ArgumentException("Selected epoch PID missing from verified G1 source");return result.ToArray();
    }
}
