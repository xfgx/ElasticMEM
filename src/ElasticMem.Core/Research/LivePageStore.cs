using System.Security.Cryptography;
namespace ElasticMem.Core.Research;
/// <summary>Cooperative owned-data backend. Never intercepts foreign process memory.</summary>
public sealed class LivePageStore:IDisposable
{
    sealed record Entry(byte[] Payload,byte[] Hash,bool Raw);
    readonly Dictionary<long,Entry> _pages=new();
    readonly Dictionary<long,LinkedListNode<(long Key,byte[] Bytes)>> _hot=new();
    readonly LinkedList<(long Key,byte[] Bytes)> _lru=new();
    readonly IPageCodec? _codec;readonly int _maxPages,_hotPages;readonly object _gate=new();bool _disposed;
    long _payload,_reads,_writes,_hits;
    public LivePageStore(string mode,int maxPages=16384,int hotPages=256)
    {if(mode is not("raw" or "zstd1")||maxPages is <1 or >65536||hotPages<0||hotPages>Math.Min(maxPages,1024))throw new ArgumentException("Invalid bounded store configuration");_maxPages=maxPages;_hotPages=hotPages;if(mode=="zstd1")_codec=new PortablePageCodec("zstd1");}
    void Alive(){if(_disposed)throw new ObjectDisposedException(nameof(LivePageStore));}
    public void Put(long key,byte[] page)
    {
        ArgumentNullException.ThrowIfNull(page);if(key<0||page.Length!=4096)throw new ArgumentException("Nonnegative key and exact4096B page required");
        lock(_gate)
        {
            Alive();if(!_pages.ContainsKey(key)&&_pages.Count==_maxPages)throw new InvalidOperationException("Page capacity exceeded");
            // Caller storage is never retained. Compute everything before mutating prior entry.
            byte[] raw=(byte[])page.Clone();byte[] hash=SHA256.HashData(raw);byte[]? packed=_codec?.Encode(raw);bool fallback=packed==null||packed.Length>=4096;
            var entry=new Entry(fallback?raw:packed!,hash,fallback);
            if(_pages.TryGetValue(key,out var previous))_payload-=previous.Payload.Length;
            _pages[key]=entry;_payload+=entry.Payload.Length;DropHot(key);_writes++;
        }
    }
    void DropHot(long key){if(_hot.Remove(key,out var node))_lru.Remove(node);}
    public byte[] Get(long key)
    {
        lock(_gate)
        {
            Alive();if(!_pages.TryGetValue(key,out var entry))throw new KeyNotFoundException("Page not stored");
            byte[] bytes;if(_hot.TryGetValue(key,out var node)){_lru.Remove(node);_lru.AddFirst(node);bytes=node.Value.Bytes;_hits++;}
            else
            {
                bytes=entry.Raw?(byte[])entry.Payload.Clone():_codec!.Decode(entry.Payload,4096);
                if(!SHA256.HashData(bytes).AsSpan().SequenceEqual(entry.Hash))throw new InvalidDataException("Live page integrity failure");
                if(_hotPages>0){var added=_lru.AddFirst((key,bytes));_hot.Add(key,added);if(_hot.Count>_hotPages){var tail=_lru.Last!;_hot.Remove(tail.Value.Key);_lru.RemoveLast();}}
            }
            _reads++;return(byte[])bytes.Clone(); // client owns result; cannot corrupt cached/store bytes
        }
    }
    public bool Delete(long key){lock(_gate){Alive();if(!_pages.Remove(key,out var entry))return false;_payload-=entry.Payload.Length;DropHot(key);return true;}}
    public object Stats(){lock(_gate){Alive();return new{schema="elasticmem.live.store.v1",pages=_pages.Count,logical_bytes=(long)_pages.Count*4096,payload_bytes=_payload,raw_fallback_pages=_pages.Count(x=>x.Value.Raw),hot_pages=_hot.Count,hot_bytes=(long)_hot.Count*4096,reads=_reads,writes=_writes,cache_hits=_hits,max_pages=_maxPages,max_hot_pages=_hotPages,physical_ram_saved_bytes=(long?)null,scope="cooperative_owned_pages_not_foreign_memory",limitations=new[]{"Payload/hot byte counts exclude managed metadata,codec workspaces,GC and process overhead; use measured process counters","Caller must release original buffers; keeping originals defeats RAM reduction","No disk persistence; process exit loses all stored pages; not a pagefile or transparent backend"}};}}
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_pages.Clear();_hot.Clear();_lru.Clear();_codec?.Dispose();_payload=0;}}
}
