using System.ComponentModel;
using System.Runtime.InteropServices;
using ElasticMem.Core.Backend.Vmm;
using ElasticMem.DriverClient;
namespace ElasticMem.Vmm;

// An integration building block for a CUSTOM VMM which owns the supplied WHP partition.
// It does not open vmwp, Hyper-V managed VM memory, another PID, or guest process addresses.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SysVmStore:IVmCompressedStore,IDisposable
{
    readonly KernelPageStore store;
    public SysVmStore(uint pages=4096,ulong quotaBytes=16UL*1024*1024){store=new(KernelPageStore.StoreMode.Xpress);try{store.ConfigurePager(pages,quotaBytes);}catch{store.Dispose();throw;}}
    public VmStoreReceipt Put(uint key,byte[] bytes){var r=store.PutCompressed(key,bytes);return new(r.Generation,r.PayloadBytes,r.AllocationBytes);}
    public byte[] Get(uint key,ulong generation)=>store.GetVersioned(key,generation);
    public bool Delete(uint key,ulong generation)=>store.DeleteVersioned(key,generation);
    public void Dispose()=>store.Dispose();
}
public sealed class WhpPageBacking:IVmPageBacking,IDisposable
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr VirtualAlloc(IntPtr address,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool VirtualFree(IntPtr address,nuint size,uint type);
    [DllImport("WinHvPlatform.dll")]static extern int WHvMapGpaRange(IntPtr partition,IntPtr source,ulong guestAddress,ulong bytes,uint flags);
    [DllImport("WinHvPlatform.dll")]static extern int WHvUnmapGpaRange(IntPtr partition,ulong guestAddress,ulong bytes);
    [StructLayout(LayoutKind.Sequential)]struct MemoryInfo{public IntPtr BaseAddress,AllocationBase;public uint AllocationProtect,Padding;public nuint RegionSize;public uint State,Protect,Type;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern nuint VirtualQuery(IntPtr address,out MemoryInfo info,nuint length);
    uint NativeState(int page){if(VirtualQuery(Address(page),out var info,(nuint)Marshal.SizeOf<MemoryInfo>())==0)throw new Win32Exception(Marshal.GetLastWin32Error());return info.State;}
    readonly IntPtr partition;readonly ulong guestBase;readonly int pages;IntPtr memory;readonly bool[] mapped;
    bool[] committed;bool disposed;
    public WhpPageBacking(IntPtr ownedPartition,ulong guestBase,int pages)
    {
        if(!OperatingSystem.IsWindows()||ownedPartition==IntPtr.Zero||guestBase%4096!=0||pages is <1 or >4096)throw new ArgumentException("Own WHP partition, aligned GPA and 1..4096 pages required");
        _=checked(guestBase+(ulong)pages*4096);partition=ownedPartition;this.guestBase=guestBase;this.pages=pages;this.mapped=new bool[pages];committed=new bool[pages];
        memory=VirtualAlloc(IntPtr.Zero,(nuint)pages*4096,0x3000,4);if(memory==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        Array.Fill(committed,true);
        // The custom VMM maps initial pages explicitly after construction under its barrier.
    }
    IntPtr Address(int page){ObjectDisposedException.ThrowIf(disposed,this);if((uint)page>=(uint)pages)throw new ArgumentOutOfRangeException(nameof(page));return IntPtr.Add(memory,checked(page*4096));}
    ulong Gpa(int page)=>checked(guestBase+(ulong)page*4096);
    public byte[] Read(int page){var a=Address(page);if(!committed[page])throw new InvalidOperationException("Page decommitted");var b=new byte[4096];Marshal.Copy(a,b,0,4096);return b;}
    public void Write(int page,byte[] bytes){var a=Address(page);if(!committed[page]||bytes.Length!=4096)throw new ArgumentException("Committed 4096-byte page required");Marshal.Copy(bytes,0,a,4096);}
    public void Map(int page){var a=Address(page);if(!committed[page])throw new InvalidOperationException("Map requires committed backing");if(mapped[page])return;Marshal.ThrowExceptionForHR(WHvMapGpaRange(partition,a,Gpa(page),4096,7));mapped[page]=true;}
    public void Unmap(int page){Address(page);if(!mapped[page])throw new InvalidOperationException("Page not mapped");Marshal.ThrowExceptionForHR(WHvUnmapGpaRange(partition,Gpa(page),4096));mapped[page]=false;}
    public void Decommit(int page){var a=Address(page);if(mapped[page])throw new InvalidOperationException("Unmap before decommit");if(!VirtualFree(a,4096,0x4000))throw new Win32Exception(Marshal.GetLastWin32Error());committed[page]=false;if(NativeState(page)!=0x2000)throw new InvalidDataException("Native backing not reserved after decommit");}
    public void Commit(int page){var a=Address(page);if(committed[page])return;var result=VirtualAlloc(a,4096,0x1000,4);if(result!=a)throw new Win32Exception(Marshal.GetLastWin32Error());committed[page]=true;if(NativeState(page)!=0x1000)throw new InvalidDataException("Native backing not committed");}
    public bool IsReserved(int page)=>NativeState(page)==0x2000;
    public bool IsMapped(int page){Address(page);return mapped[page];}
    public bool IsCommitted(int page){Address(page);return committed[page]&&NativeState(page)==0x1000;}
    public void Dispose()
    {
        if(disposed)return;
        // Owner MUST stop all CPU/I/O activity before disposal. No finalizer frees guest memory.
        for(int i=0;i<pages;i++)if(mapped[i])Unmap(i);
        if(!VirtualFree(memory,0,0x8000))throw new Win32Exception(Marshal.GetLastWin32Error());memory=IntPtr.Zero;disposed=true;
    }
}
