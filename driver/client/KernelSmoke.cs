using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
namespace ElasticMem.DriverClient;
[SupportedOSPlatform("windows")]
public static class KernelSmoke
{
    public static object Run(int iterations=1000,CancellationToken ct=default)
    {
        if(iterations is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(iterations));
        int checks=0;void Check(bool b,string what){checks++;if(!b)throw new InvalidDataException(what);}
        var rows=new List<object>();
        ct.ThrowIfCancellationRequested();
        foreach(var mode in new[]{KernelPageStore.StoreMode.Raw,KernelPageStore.StoreMode.Xpress})
        {
            using var a=new KernelPageStore(mode);using var b=new KernelPageStore(mode);
            var before=a.Stats();var expected=new Dictionary<uint,byte[]>();var rng=new Random(24019);
            Check(before.Pages==0,"new session empty");
            byte[] Missing=Array.Empty<byte>();
            for(int i=0;i<iterations;i++)
            {
                ct.ThrowIfCancellationRequested();
                uint key=(uint)rng.Next(64);byte[] page=new byte[4096];if(i%2==0)rng.NextBytes(page);else Array.Fill(page,(byte)(i%251));
                a.Put(key,page);expected[key]=(byte[])page.Clone();page[0]^=255;
                Check(a.Get(key).AsSpan().SequenceEqual(expected[key]),"put/get full bytes");
                bool absent=false;try{Missing=b.Get(key);}catch(Win32Exception ex)when(ex.NativeErrorCode==1168){absent=true;}finally{Array.Clear(Missing);}
                Check(absent,"handle isolation");
            }
            Parallel.For(0,64,new ParallelOptions{CancellationToken=ct},k=>{byte[] page=Enumerable.Repeat((byte)k,4096).ToArray();a.Put((uint)k,page);if(!a.Get((uint)k).AsSpan().SequenceEqual(page))throw new InvalidDataException("parallel calls");});
            var full=a.Stats();Check(full.Pages==64 && full.RawPages+full.CompressedPages==64,"counters");
            Check(mode!=KernelPageStore.StoreMode.Raw || full.PayloadBytes==64*4096,"raw payload");
            Check(mode!=KernelPageStore.StoreMode.Xpress || full.CompressedPages>0,"native XPRESS exercised");
            Check(a.Delete(63)&&!a.Delete(63),"delete missing semantics");
            byte[] h=new byte[16];h[0]=1;
            Check(a.RejectProbe(0x8000E008,h,4095)!=0,"GET wrong output length rejected");
            h[0]=2;Check(a.RejectProbe(0x8000E000,h,104)!=0,"ABI version rejected");h[0]=1;
            h[12]=1;Check(a.RejectProbe(0x8000E000,h,104)!=0,"reserved rejected");h[12]=0;
            Check(a.RejectProbe(0x8000E004,h,0)!=0,"short PUT rejected");
            Check(a.RejectProbe(0x8000E3FC,h,0)!=0,"unknown IOCTL rejected");
            h[4]=0;h[5]=16;Check(a.RejectProbe(0x8000E008,h,4096)!=0,"out of range key rejected");h[5]=0;
            h[8]=1;Check(a.RejectProbe(0x8000E014,h,0)!=0,"mode change on nonempty store rejected");
            a.Reset();var after=a.Stats();Check(after.Pages==0 && after.PayloadBytes==0,"reset");
            Check(after.SessionAllocationBytes==before.SessionAllocationBytes && after.GlobalAllocationBytes==before.GlobalAllocationBytes,"tracked allocation rollback");
            using(var temporary=new KernelPageStore(mode)){temporary.Put(0,new byte[4096]);Check(temporary.Stats().Pages==1,"cleanup fixture");}
            Check(a.Stats().GlobalAllocationBytes==before.GlobalAllocationBytes,"close frees session/page allocations");
            // RAW storage reaches 16MiB quota before 4096 nodes plus table would fit; failure preserves data.
            using(var quota=new KernelPageStore(KernelPageStore.StoreMode.Raw))
            {bool refused=false;for(uint k=0;k<4096;k++){ct.ThrowIfCancellationRequested();try{quota.Put(k,new byte[4096]);}catch(Win32Exception ex)when(ex.NativeErrorCode==1816||ex.NativeErrorCode==8||ex.NativeErrorCode==1450){refused=true;break;}}
                Check(refused && quota.Stats().Pages==(16*1024*1024-4096*8)/(4096+24),"bounded session quota at planned capacity");Check(quota.Get(0).All(x=>x==0),"quota failure retains prior values");}
            Check(a.Stats().GlobalAllocationBytes==before.GlobalAllocationBytes,"quota session cleanup");
            var handles=new List<KernelPageStore>();bool sessionRefused=false;
            try{for(int i=0;i<7;i++)handles.Add(new KernelPageStore(mode));}catch(Win32Exception){sessionRefused=true;}
            finally{foreach(var handle in handles)handle.Dispose();}
            Check(sessionRefused && handles.Count==6,"eight-session limit");Check(a.Stats().GlobalAllocationBytes==before.GlobalAllocationBytes,"session limit cleanup");
            rows.Add(new{mode=mode.ToString(),before,full,after,full_bytes_verified=true});
        }
        return new{schema="elasticmem.kernel.smoke.v1",status="passed_owned_driver_smoke_only",checks,iterations,rows,
            transparent_foreign_memory_supported=false,physical_ram_saved_bytes=(long?)null,
            limitations=new[]{"XPRESS kernel codec, not zstd; this is explicit IOCTL store, not Windows Memory Manager replacement","Tracked pool request sizes exclude allocator/KMDF/IO-manager overhead; paged pool is NOT measured unique physical RAM","Synthetic fixtures only; driver verifier, reboot, unload and non-admin checks require separate VM runs","Volatile: process handle close/reboot loses pages; never sole copy of valuable data"}};
    }
}
