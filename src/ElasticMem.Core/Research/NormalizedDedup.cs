using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ElasticMem.Core.Research;
public record SnapshotRegion(ulong Base, ulong Size);
public record SnapshotPage(ulong Address, byte[] Bytes);
public record ProcessSnapshot(int Pid, long StartTicks, SnapshotRegion[] Regions, SnapshotPage[] Pages);
public record DedupResult(long InputBytes, long RawDedupBytes, long NormalizedDedupBytes, long RawBrotliBytes, long NormalizedBrotliBytes, int Pages, int RawUnique, int NormalizedUnique, long CandidatePointerWords, bool Roundtrip, string[] Limitations, long BrotliOnlyBytes);
public static class NormalizedDedup
{
    public static DedupResult Run(IReadOnlyList<ProcessSnapshot> snapshots, Action<string>? log = null, CancellationToken ct = default)
    {
        var raw = new Dictionary<string, List<byte[]>>(); var norm = new Dictionary<string, List<byte[]>>();
        long total=0, pointerWords=0, regionBytes=0, masks=0; int pages=0;
        long rawBytes=0, normBytes=0, rawCompressed=0, normCompressed=0, brotliOnly=0;
        foreach (var snap in snapshots)
        {
            if (snap.Regions.Length > 65535) throw new InvalidDataException("Region catalog exceeds 16-bit index");
            regionBytes += snap.Regions.Length * 16L + 16; // bases+sizes, PID+process-instance binding
            for (int i=0; i<snap.Regions.Length; i++)
            {
                var r=snap.Regions[i]; if (r.Size == 0 || r.Base > ulong.MaxValue-r.Size) throw new InvalidDataException("Invalid region");
                if (i>0 && snap.Regions[i-1].Base+snap.Regions[i-1].Size>r.Base) throw new InvalidDataException("Overlapping/unsorted regions");
            }
            foreach (var page in snap.Pages)
            {
                ct.ThrowIfCancellationRequested(); if (page.Bytes.Length != 4096) throw new InvalidDataException("Only full 4 KiB pages supported");
                total+=4096; pages++; var normalized=(byte[])page.Bytes.Clone(); var mask=new byte[64];
                for (int w=0; w<512; w++)
                {
                    ulong value=BinaryPrimitives.ReadUInt64LittleEndian(page.Bytes.AsSpan(w*8,8));
                    int id=FindRegion(snap.Regions,value); if (id<0) continue;
                    ulong offset=value-snap.Regions[id].Base; if(offset>0x0000FFFFFFFFFFFFUL) continue;
                    BinaryPrimitives.WriteUInt64LittleEndian(normalized.AsSpan(w*8,8), ((ulong)id << 48) | offset);
                    mask[w/8]|=(byte)(1<<(w%8)); pointerWords++;
                }
                var restored=(byte[])normalized.Clone();
                for(int w=0;w<512;w++) if((mask[w/8]&(1<<(w%8)))!=0)
                {
                    ulong token=BinaryPrimitives.ReadUInt64LittleEndian(restored.AsSpan(w*8,8)); int id=(int)(token>>48); ulong offset=token&0x0000FFFFFFFFFFFFUL;
                    if(id>=snap.Regions.Length || offset>=snap.Regions[id].Size) throw new InvalidDataException("Invalid normalization token");
                    BinaryPrimitives.WriteUInt64LittleEndian(restored.AsSpan(w*8,8),snap.Regions[id].Base+offset);
                }
                if(!restored.AsSpan().SequenceEqual(page.Bytes))throw new InvalidDataException("Roundtrip mismatch");
                brotliOnly+=CompressedSize(page.Bytes)+4;
                if(AddUnique(raw,page.Bytes)){rawBytes+=4096;rawCompressed+=CompressedSize(page.Bytes)+4;}
                if(AddUnique(norm,normalized)){normBytes+=4096;normCompressed+=CompressedSize(normalized)+4;}
                masks+=64; // Store every bitmap; no free zero masks.
            }
            log?.Invoke($"PID {snap.Pid}: {snap.Pages.Length} pages, reversible candidate-pointer normalization");
        }
        // Raw format pays page/process bindings only. Normalized format additionally needs target regions and masks.
        long common=pages*16L+regionBytes+64; // unique-ID+PID+virtual page address, header
        long rawCommon=pages*16L+snapshots.Count*16L+64;
        return new(total,rawBytes+rawCommon,normBytes+common+masks,rawCompressed+rawCommon,normCompressed+common+masks,pages,raw.Sum(x=>x.Value.Count),norm.Sum(x=>x.Value.Count),pointerWords,true,
            new[]{"Offline cold-store candidate only; no live page sharing or RAM reclaim", "Words in captured address ranges are candidates, not proven pointers; decoding is lossless either way", "Region IDs are address-order ordinals, not semantic cross-process type IDs", "All 64-byte masks, refs, region mappings and compressed length prefixes counted; allocator/runtime overhead not physical RSS", "Brotli byte counts are serialization sizes; latency and Windows page combining not measured", "Snapshots are bounded non-atomic samples; unreadable/not-captured pages excluded", "Ordinal-region normalization is a closed historical comparator, not semantic normalization", "Brotli-only baseline uses the same per-page Fastest codec, page bindings and length prefixes, no dedup"},brotliOnly+rawCommon);
    }
    private static int FindRegion(SnapshotRegion[] r,ulong v){int lo=0,hi=r.Length-1;while(lo<=hi){int mid=(lo+hi)/2;if(v<r[mid].Base)hi=mid-1;else if(v-r[mid].Base>=r[mid].Size)lo=mid+1;else return mid;}return -1;}
    private static bool AddUnique(Dictionary<string,List<byte[]>> d,byte[] data)
    {
        string hash=Convert.ToHexString(SHA256.HashData(data));if(!d.TryGetValue(hash,out var bucket)){bucket=new();d[hash]=bucket;}
        if(bucket.Any(b=>b.AsSpan().SequenceEqual(data)))return false;bucket.Add(data);return true;
    }
    private static long CompressedSize(byte[] b){using var m=new MemoryStream();using(var z=new BrotliStream(m,CompressionLevel.Fastest,true))z.Write(b);return m.Length;}
}
