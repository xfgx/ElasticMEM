using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class EpochNewTests
{
 public static int Run()
 {
  int checks=0;void C(bool ok,string why){checks++;if(!ok)throw new InvalidDataException(why);}
  bool[][] a=Enumerable.Range(0,40).Select(t=>new[]{t%10==0,false}).ToArray();using var m=JsonDocument.Parse(JsonSerializer.Serialize(EpochModel.Run(a,1,new[]{10d},10000,initialResident:new[]{false,false})));
  C(m.RootElement.GetProperty("curves").GetArrayLength()==9,"long grid9");C(m.RootElement.GetProperty("curves")[0].GetProperty("average_end_epoch_offloaded_bytes").GetDouble()<=4096,"never observed not offloaded");
  bool[][] known=a.Select(row=>new[]{true,true}).ToArray();using var diagnostic=JsonDocument.Parse(JsonSerializer.Serialize(EpochDiagnostics.Run(a,known,null,Enumerable.Repeat(1d,40).ToArray(),CancellationToken.None)));
  C(diagnostic.RootElement.GetProperty("ever_observed_pages").GetInt32()==1,"never observed excluded v2");C(diagnostic.RootElement.GetProperty("initial_residency_available").GetBoolean()==false,"no invented old baseline");
  ulong[] addresses=Enumerable.Range(1,16).Select(i=>(ulong)i*4096).ToArray();bool[][] trace=Enumerable.Range(0,40).Select(t=>Enumerable.Range(0,16).Select(p=>t<20?t%3==0:t%5==0&&p<8).ToArray()).ToArray();bool[][] k=trace.Select(row=>Enumerable.Repeat(true,16).ToArray()).ToArray();
  foreach(int size in new[]{1,4,8,16})foreach(string policy in new[]{"adjacency","coaccess"})
  {
   var sim=CausalGrouping.Simulate(addresses,trace,k,size,2,policy);C(sim.Excluded==0,"complete common epochs");C(sim.Events.All(e=>e.Useful>0&&e.Pages.Length<=size&&e.Pages.Distinct().Count()==e.Pages.Length),"causal unique bounded groups");
   // Alter future after epoch30: all earlier events and partner sets must remain identical.
   bool[][] changed=trace.Select((row,t)=>t<30?(bool[])row.Clone():row.Select(x=>!x).ToArray()).ToArray();var other=CausalGrouping.Simulate(addresses,changed,k,size,2,policy);C(JsonSerializer.Serialize(sim.Events.Where(e=>e.Epoch<30))==JsonSerializer.Serialize(other.Events.Where(e=>e.Epoch<30)),"no future leakage");
   k[25][0]=false;var partial=CausalGrouping.Simulate(addresses,trace,k,size,2,policy);C(partial.Excluded==1&&partial.Events.All(e=>e.Epoch!=25),"common unknown epoch excluded not death");k[25][0]=true;
  }
  // A hot partner must never be evicted into a cold block: page0 always hot,test page1 alone returns.
  bool[][] hot=Enumerable.Range(0,40).Select(t=>new[]{true,t==26,false,false}).ToArray();var hs=CausalGrouping.Simulate(new ulong[]{4096,8192,12288,16384},hot,hot.Select(r=>new[]{true,true,true,true}).ToArray(),4,2,"coaccess");C(hs.Events.All(e=>!e.Pages.Contains(0)),"hot partners excluded");
  string file=Path.Combine(Path.GetTempPath(),"ElasticMemEET3-"+Guid.NewGuid()+".eet");try{EpochTraceFile.Write(file,10,1,addresses,Enumerable.Repeat(true,16).ToArray(),trace,k,Enumerable.Repeat(false,16).ToArray());var read=EpochTraceFile.Read(file,40);C(read.InitialResident!=null&&read.InitialResident.All(x=>!x),"v3 baseline roundtrip");C(read.Activity.Length==40,"v3 planned full trace");byte[] b=File.ReadAllBytes(file);File.WriteAllBytes(file,b[..^1]);bool rejected=false;try{EpochTraceFile.Read(file);}catch(InvalidDataException){rejected=true;}C(rejected,"v3 truncated rejected");}finally{File.Delete(file);}
  using(var run=JsonDocument.Parse(JsonSerializer.Serialize(CausalGrouping.Run(new(10,1,addresses,addresses.Select(x=>new byte[4096]).ToArray()),trace,k,CancellationToken.None)))){C(run.RootElement.GetProperty("rows").GetArrayLength()==63,"all causal threshold/size/policy rows");C(run.RootElement.GetProperty("rows")[0].GetProperty("block_pages").GetInt32()==1,"one-page same-cohort control");}
  return checks;
 }
}
