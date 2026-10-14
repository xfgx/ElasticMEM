using System.Diagnostics;
using System.Buffers;
using System.Text.Json;
namespace ElasticMem.Core.Research;
/// <summary>Sequential exchange with an owned child. After any error, caller must terminate that child, not reuse the stream.</summary>
public static class LiveStoreRpc
{
    public static JsonElement Exchange(Process child,object request,CancellationToken ct,TimeSpan? timeout=null)
    {
        ct.ThrowIfCancellationRequested();if(child.HasExited)throw new IOException("IPC child exited");
        string line=JsonSerializer.Serialize(request);if(line.Length>LiveStoreProtocol.MaxFrameChars)throw new ArgumentException("IPC request exceeds frame cap");
        using var rpc=CancellationTokenSource.CreateLinkedTokenSource(ct);rpc.CancelAfter(timeout??TimeSpan.FromSeconds(10));
        try
        {
            child.StandardInput.WriteLineAsync(line.AsMemory(),rpc.Token).WaitAsync(rpc.Token).GetAwaiter().GetResult();
            child.StandardInput.FlushAsync(rpc.Token).WaitAsync(rpc.Token).GetAwaiter().GetResult();
            // A bounded read, including peers that emit no newline. StreamReader itself buffers pipe reads.
            char[] chars=ArrayPool<char>.Shared.Rent(LiveStoreProtocol.MaxFrameChars);var one=new char[1];int n=0;
            try
            {
            while(true)
            {
                int got=child.StandardOutput.ReadAsync(one.AsMemory(),rpc.Token).AsTask().WaitAsync(rpc.Token).GetAwaiter().GetResult();
                if(got==0)throw new InvalidDataException("IPC reply ended before newline");
                if(one[0]=='\n')break;
                if(n==LiveStoreProtocol.MaxFrameChars)throw new InvalidDataException("IPC reply exceeds frame cap");
                chars[n++]=one[0];
            }
            using var d=JsonDocument.Parse(new string(chars,0,n));
            if(d.RootElement.ValueKind!=JsonValueKind.Object || !d.RootElement.TryGetProperty("ok",out var ok) || ok.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("IPC reply missing boolean status");
            return d.RootElement.Clone();
            }
            finally{ArrayPool<char>.Shared.Return(chars,clearArray:true);}
        }
        catch(OperationCanceledException ex)when(!ct.IsCancellationRequested){throw new TimeoutException("Whole IPC RPC exceeded deadline",ex);}
        catch(JsonException){throw new InvalidDataException("IPC reply is not valid JSON; payload not logged");}
    }
}
