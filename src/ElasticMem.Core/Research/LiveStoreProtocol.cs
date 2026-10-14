using System.Text;
using System.Text.Json;
namespace ElasticMem.Core.Research;
public static class LiveStoreProtocol
{
    // 4096 bytes -> 5464 base64 chars; default JSON may escape every '+' to six chars.
    // 12000 rejected valid fixture key62 (12138 chars). 65536 safely bounds worst escaped page + header.
    public const int MaxFrameChars=65536;
    /// <summary>Bounded local stdio protocol. No network listener; caller owns pipe/process.</summary>
    public static int Run(TextReader input,TextWriter output,string mode="zstd1")
    {
        using var store=new LivePageStore(mode);
        while(true)
        {
            var line=new StringBuilder();int ch;
            while((ch=input.Read())!=-1&&ch!='\n'){if(line.Length>=MaxFrameChars){output.WriteLine("{\"ok\":false,\"error\":\"request_too_large\"}");output.Flush();return 1;}if(ch!='\r')line.Append((char)ch);}
            if(ch==-1&&line.Length==0)return 0;
            try
            {
                using var doc=JsonDocument.Parse(line.ToString());var d=doc.RootElement;string? cmd=d.GetProperty("cmd").GetString();object response;
                switch(cmd)
                {
                    case "put":store.Put(d.GetProperty("key").GetInt64(),Convert.FromBase64String(d.GetProperty("page_base64").GetString()!));response=new{ok=true};break;
                    case "get":response=new{ok=true,page_base64=Convert.ToBase64String(store.Get(d.GetProperty("key").GetInt64()))};break;
                    case "delete":response=new{ok=true,deleted=store.Delete(d.GetProperty("key").GetInt64())};break;
                    case "stats":response=new{ok=true,stats=store.Stats()};break;
                    case "quit":output.WriteLine("{\"ok\":true}");output.Flush();return 0;
                    default:throw new ArgumentException("Unknown command");
                }
                output.WriteLine(JsonSerializer.Serialize(response));output.Flush();
            }
            catch(Exception ex)when(ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FormatException)
            {output.WriteLine(JsonSerializer.Serialize(new{ok=false,error=ex.GetType().Name}));output.Flush();}
        }
    }
}
