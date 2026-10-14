using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace ElasticMem.DriverClient;

/// <summary>Native owned-memory fixture, NOT guest-process or Windows VMM paging.</summary>
[SupportedOSPlatform("windows")]
public static class PagerSmoke
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr VirtualAlloc(IntPtr address,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool VirtualFree(IntPtr address,nuint size,uint type);
    [StructLayout(LayoutKind.Sequential)]struct MemoryInfo
    {public IntPtr BaseAddress,AllocationBase;public uint AllocationProtect,Padding;public nuint RegionSize;public uint State,Protect,Type;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern nuint VirtualQuery(IntPtr address,out MemoryInfo info,nuint length);
    static void Win(bool ok){if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error());}
    static uint State(IntPtr p){Win(VirtualQuery(p,out var m,(nuint)Marshal.SizeOf<MemoryInfo>())!=0);return m.State;}
    public static object Run(int pages=64,CancellationToken ct=default)
    {
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess)throw new PlatformNotSupportedException("Windows x64 only");
        if(pages is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(pages));
        int checks=0;void Check(bool b,string what){checks++;if(!b)throw new InvalidDataException(what);}
        using var store=new KernelPageStore(KernelPageStore.StoreMode.Xpress);
        var before=store.Stats();var receipts=new KernelPagerReceipt[pages];byte[] scratch=new byte[4096];
        IntPtr memory=VirtualAlloc(IntPtr.Zero,(nuint)pages*4096,0x2000,4);Win(memory!=IntPtr.Zero);
        try
        {
            for(int i=0;i<pages;i++)
            {
                ct.ThrowIfCancellationRequested();IntPtr page=IntPtr.Add(memory,i*4096);
                Win(VirtualAlloc(page,4096,0x1000,4)==page);Array.Fill(scratch,(byte)(i%251));scratch[0]=(byte)(i^0x5a);
                Marshal.Copy(scratch,0,page,4096);Array.Clear(scratch);Marshal.Copy(page,scratch,0,4096);
                // Unsupported old SYS / nonbeneficial compression fails HERE, before source decommit.
                var receipt=store.PutCompressed((uint)i,scratch);receipts[i]=receipt;
                Check(receipt.AllocationBytes<4096&&receipt.PayloadBytes>0,"beneficial SYS receipt");
                Array.Clear(scratch);Win(VirtualFree(page,4096,0x4000));Check(State(page)==0x2000,"VirtualQuery confirms decommitted own page");
            }
            bool staleRead=false,staleDelete=false;ulong stale=checked(receipts[0].Generation+1);
            byte[]? invalid=null;
            try{invalid=store.GetVersioned(0,stale);}catch(Win32Exception e)when(e.NativeErrorCode==1306){staleRead=true;}finally{if(invalid!=null)Array.Clear(invalid);}
            try{store.DeleteVersioned(0,stale);}catch(Win32Exception e)when(e.NativeErrorCode==1306){staleDelete=true;}
            Check(staleRead&&staleDelete,"native stale generation read/delete rejected");
            var evicted=store.Stats();Check(evicted.Pages==pages&&evicted.RawPages==0&&evicted.CompressedPages==(ulong)pages,"all fixture pages are compressed in SYS");
            // Observe allocation ledger, not unique physical RAM or throughput of real apps.
            for(int i=0;i<pages;i++)
            {
                ct.ThrowIfCancellationRequested();IntPtr page=IntPtr.Add(memory,i*4096);var receipt=receipts[i];
                byte[] restored=store.GetVersioned((uint)i,receipt.Generation);
                try
                {
                    Check(restored.Length==4096&&restored[0]==(byte)(i^0x5a)&&restored.Skip(1).All(x=>x==(byte)(i%251)),"versioned full byte restore");
                    Win(VirtualAlloc(page,4096,0x1000,4)==page);Marshal.Copy(restored,0,page,4096);
                    Check(State(page)==0x1000,"restored backing committed");
                    Marshal.Copy(page,scratch,0,4096);Check(scratch.AsSpan().SequenceEqual(restored),"native backing byte verification");Array.Clear(scratch);
                    Check(store.DeleteVersioned((uint)i,receipt.Generation),"delete exact consumed generation");
                }finally{Array.Clear(restored);}
            }
            var after=store.Stats();Check(after.Pages==0&&after.SessionAllocationBytes==before.SessionAllocationBytes,"SYS page allocations returned to baseline");
            return new{schema="elasticmem.sys.owned_pager.smoke.v1",status="passed_owned_fixture_only",checks,pages,
                backend="ElasticMemStore SYS XPRESS compressed-only generation protocol",source_virtualquery_decommit_verified=true,
                released_owned_backing_commit_bytes=(long)pages*4096,retained_driver_session_allocation_bytes=evicted.SessionAllocationBytes,
                accounted_owned_commit_minus_driver_allocation_bytes=(long)pages*4096-(long)evicted.SessionAllocationBytes,
                physical_ram_saved_bytes=(long?)null,guest_vm_memory_paged=false,foreign_process_memory_supported=false,
                native_windows_execution=true,before,evicted,after,
                limitations=new[]{"Synthetic owned pages only; NOT Chrome/Telegram or WHP guest paging","Allocation ledger excludes allocator rounding, shared workspaces, managed scratch, OS and host overhead","Physical RAM benefit, HVCI, Verifier, sustained stress NOT proven"}};
        }
        finally{Array.Clear(scratch);Win(VirtualFree(memory,0,0x8000));}
    }
}
