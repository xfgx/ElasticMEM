using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class LiveBackendTests
{
    public static int Run()
    {
        int n=0;void Check(bool b,string s){n++;if(!b)throw new InvalidDataException(s);}
        foreach(string mode in new[]{"raw","zstd1"})foreach(int hot in new[]{0,2})
        {
            using var store=new LivePageStore(mode,8,hot);var rnd=new Random(23019);var expected=new Dictionary<long,byte[]>();
            for(int i=0;i<8;i++){var b=new byte[4096];if(i%2==0)rnd.NextBytes(b);else Array.Fill(b,(byte)i);expected[i]=(byte[])b.Clone();store.Put(i,b);b[0]^=255;}
            for(int i=0;i<100;i++){int k=rnd.Next(8);Check(store.Get(k).AsSpan().SequenceEqual(expected[k]),"owned bytes/read/cache "+mode);var got=store.Get(k);got[0]^=255;Check(store.Get(k).AsSpan().SequenceEqual(expected[k]),"result ownership");if(i%7==0){var page=new byte[4096];rnd.NextBytes(page);expected[k]=page;store.Put(k,page);Check(store.Get(k).AsSpan().SequenceEqual(page),"overwrite invalidates cache");}}
            bool refused=false;try{store.Put(9,new byte[4096]);}catch(InvalidOperationException){refused=true;}Check(refused,"capacity fail closed");
            Check(store.Delete(0)&&!store.Delete(0),"delete semantics");refused=false;try{store.Get(0);}catch(KeyNotFoundException){refused=true;}Check(refused,"deleted read fails");store.Put(0,expected[0]);
            using(var d=JsonDocument.Parse(JsonSerializer.Serialize(store.Stats()))){Check(d.RootElement.GetProperty("pages").GetInt32()==8,"live page count");Check(d.RootElement.GetProperty("hot_pages").GetInt32()<=hot,"bounded hot cache");}
            store.Dispose();refused=false;try{store.Get(0);}catch(ObjectDisposedException){refused=true;}Check(refused,"get after dispose");
        }
        foreach(var config in new[]{("bad",8,0),("raw",0,0),("zstd1",8,9)}){bool refused=false;try{using var s=new LivePageStore(config.Item1,config.Item2,config.Item3);}catch(ArgumentException){refused=true;}Check(refused,"invalid configuration");}
        var bytes=Enumerable.Repeat((byte)0x5A,4096).ToArray();string request=JsonSerializer.Serialize(new{cmd="put",key=0,page_base64=Convert.ToBase64String(bytes)})+"\n{\"cmd\":\"get\",\"key\":0}\n{\"cmd\":\"delete\",\"key\":0}\n{\"cmd\":\"get\",\"key\":0}\n{\"cmd\":\"put\",\"key\":1,\"page_base64\":\"not-base64\"}\n{\"cmd\":\"quit\"}\n";
        using(var output=new StringWriter()){Check(LiveStoreProtocol.Run(new StringReader(request),output)==0,"protocol exits cleanly");var lines=output.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries);Check(lines.Length==6,"one response perrequest");using var read=JsonDocument.Parse(lines[1]);Check(Convert.FromBase64String(read.RootElement.GetProperty("page_base64").GetString()!).AsSpan().SequenceEqual(bytes),"stdio page roundtrip");using var gone=JsonDocument.Parse(lines[3]);Check(!gone.RootElement.GetProperty("ok").GetBoolean(),"stdio deleted read rejected");using var malformed=JsonDocument.Parse(lines[4]);Check(!malformed.RootElement.GetProperty("ok").GetBoolean(),"bad base64 rejected");}
        using(var output=new StringWriter()){Check(LiveStoreProtocol.Run(new StringReader(new string('x',12001)),output)==1,"stdio oversized frame terminates");Check(!output.ToString().Contains(new string('x',8)),"malformed contents not echoed");}
        return n;
    }
}
