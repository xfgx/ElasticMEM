using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using ElasticMem.Core.Research;
if(args.Contains("--repro-023"))
{
    byte[] page=new byte[4096];new Random(unchecked(23019+62*65537)).NextBytes(page.AsSpan(0,128));page.AsSpan(128).Fill(62);BitConverter.TryWriteBytes(page.AsSpan(128,4),0);
    string request=JsonSerializer.Serialize(new{cmd="put",key=62,page_base64=Convert.ToBase64String(page)});
    using var output=new StringWriter();int code=LiveStoreProtocol.Run(new StringReader(request+"\n{\"cmd\":\"quit\"}\n"),output,"raw");
    Console.WriteLine(JsonSerializer.Serialize(new{fixture_key=62,page_bytes=4096,request_characters=request.Length,protocol_exit=code,responses=output.ToString()}));return 0;
}
if(args.Contains("--silent")){Thread.Sleep(30000);return 0;}
if(args.Contains("--oversize")){Console.Write(new string('x',LiveStoreProtocol.MaxFrameChars+1));Console.Out.Flush();Thread.Sleep(30000);return 0;}
if(args.Contains("--server"))
{
    using var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8,true);
    using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
    return LiveStoreProtocol.Run(input,output,args.Last());
}
int checks=0;void Check(bool b,string m){checks++;if(!b)throw new Exception(m);}
Process Start(string argument,string mode="raw",bool bom=false)
{
    var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,StandardInputEncoding=new UTF8Encoding(bom),StandardOutputEncoding=new UTF8Encoding(false)};
    if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    psi.ArgumentList.Add(argument);psi.ArgumentList.Add(mode);return Process.Start(psi)!;
}
void Stop(Process p){if(!p.HasExited){p.Kill();Check(p.WaitForExit(10000),"child reaped");}}
foreach(string mode in new[]{"raw","zstd1"})foreach(bool bom in new[]{false,true})
{
    using var p=Start("--server",mode,bom);
    try
    {
        var rng=new Random(24019);var expected=new List<byte[]>();
        for(int k=0;k<64;k++){byte[] b=new byte[4096];rng.NextBytes(b);expected.Add(b);var r=LiveStoreRpc.Exchange(p,new{cmd="put",key=k,page_base64=Convert.ToBase64String(b)},default);Check(r.GetProperty("ok").GetBoolean(),"put "+mode);}
        for(int k=0;k<64;k++){var r=LiveStoreRpc.Exchange(p,new{cmd="get",key=k},default);Check(Convert.FromBase64String(r.GetProperty("page_base64").GetString()!).AsSpan().SequenceEqual(expected[k]),"roundtrip "+mode);}
        // Valid pages that exceeded BOTH old request and response frame limits due to JSON '+' escaping.
        foreach(int kind in new[]{62,251,256})
        {
            byte[] page=new byte[4096];
            if(kind<256)Array.Fill(page,(byte)kind);else for(int i=0;i<page.Length;i++)page[i]=new byte[]{0xfb,0xef,0xbe}[i%3];
            string encoded=JsonSerializer.Serialize(new{cmd="put",key=200+kind,page_base64=Convert.ToBase64String(page)});
            Check(encoded.Length>12000 && encoded.Length<LiveStoreProtocol.MaxFrameChars,"old limit rejected a valid escaped page");
            Check(LiveStoreRpc.Exchange(p,new{cmd="put",key=200+kind,page_base64=Convert.ToBase64String(page)},default).GetProperty("ok").GetBoolean(),"large escaped request");
            var r=LiveStoreRpc.Exchange(p,new{cmd="get",key=200+kind},default);
            Check(Convert.FromBase64String(r.GetProperty("page_base64").GetString()!).AsSpan().SequenceEqual(page),"large escaped response");
        }
        var bad=LiveStoreRpc.Exchange(p,new{cmd="put",key=0,page_base64="bad"},default);Check(!bad.GetProperty("ok").GetBoolean()&&bad.GetProperty("error").GetString()=="FormatException","safe rejection class");
        var old=LiveStoreRpc.Exchange(p,new{cmd="get",key=0},default);Check(Convert.FromBase64String(old.GetProperty("page_base64").GetString()!).AsSpan().SequenceEqual(expected[0]),"failed put atomicity");
        Check(LiveStoreRpc.Exchange(p,new{cmd="quit"},default).GetProperty("ok").GetBoolean(),"quit");Check(p.WaitForExit(10000)&&p.ExitCode==0,"clean child exit");
    }
    finally{Stop(p);}
}
foreach(string scenario in new[]{"--silent","--oversize"})
{
    using var p=Start(scenario);var timer=Stopwatch.StartNew();bool refused=false;
    try{LiveStoreRpc.Exchange(p,scenario=="--silent"?new{cmd="put",data=new string('x',11000)}:(object)new{cmd="get",key=0},default,TimeSpan.FromMilliseconds(500));}
    catch(Exception e)when(e is TimeoutException or InvalidDataException){refused=true;}
    finally{Stop(p);}
    Check(refused&&timer.Elapsed<TimeSpan.FromSeconds(8),scenario+" bounded failure");
}
using(var p=Start("--silent"))
{try{using var cts=new CancellationTokenSource();cts.Cancel();bool cancelled=false;try{LiveStoreRpc.Exchange(p,new{cmd="get"},cts.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"caller cancellation");}finally{Stop(p);}}
string root=Path.Combine(Path.GetTempPath(),"elasticmem_transport_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    using(var run=new ResearchRun(root,"live-ab","test","fixture",new{}))
    {run.WriteJson("live-ab-progress.json",new{status="owned_memory_stage_completed_ipc_pending_not_complete_run"});run.WriteJson("live-ipc-error.json",new{error_class="FormatException"});File.WriteAllText(run.FilePath("inputs/private.json"),"PRIVATE_PAYLOAD_SENTINEL");run.Seal("failed_no_valid_result","fixture");}
    string zip=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".zip");try
    {ResearchRun.ExportLogs(root,zip,false);using var z=ZipFile.OpenRead(zip);Check(z.Entries.Any(x=>x.Name=="live-ab-progress.json"),"progress exported");Check(z.Entries.Any(x=>x.Name=="live-ipc-error.json"),"diagnostic exported");Check(!z.Entries.Any(x=>x.FullName.Contains("inputs/")),"private input excluded");foreach(var e in z.Entries){using var r=new StreamReader(e.Open());Check(!r.ReadToEnd().Contains("PRIVATE_PAYLOAD_SENTINEL"),"private payload not leaked");}}
    finally{File.Delete(zip);File.Delete(zip+".sha256");}
}
finally{Directory.Delete(root,true);}
Console.WriteLine($"PASS {checks} transport/export checks; Linux owned processes, raw/zstd1, UTF8 with/without BOM, send deadline, oversized reply, cancellation, logs privacy. Windows not executed.");return 0;
