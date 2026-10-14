using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
namespace ElasticMem.DriverClient;

/// <summary>Explicit owned-data client. Never opens or modifies another process.</summary>
[SupportedOSPlatform("windows")]
public sealed class KernelPageStore : IDisposable
{
    public const int PageBytes=4096,MaxPages=4096;
    const uint StatsCode=0x8000E000,PutCode=0x8000E004,GetCode=0x8000E008,DeleteCode=0x8000E00C,ResetCode=0x8000E010,ConfigCode=0x8000E014,PutCompressedCode=0x8000E018,GetVersionedCode=0x8000E01C,DeleteVersionedCode=0x8000E020,CapabilitiesCode=0x8000E024,ConfigureStoreCode=0x8000E028;
    readonly SafeFileHandle handle;readonly object gate=new();bool disposed;uint capacity=MaxPages;
    public const uint MaximumCapacity=1048576;
    public enum StoreMode : uint { Raw=0,Xpress=1 }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputLength,[Out] byte[] output,uint outputLength,out uint returned,IntPtr overlapped);
    public KernelPageStore(StoreMode mode=StoreMode.Xpress)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows driver required");
        if(mode is not(StoreMode.Raw or StoreMode.Xpress))throw new ArgumentOutOfRangeException(nameof(mode));
        handle=CreateFileW(@"\\.\ElasticMemStore",0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error,"Open ElasticMemStore failed; requires installed signed driver and elevated administrator");}
        try{Call(ConfigCode,Header(0,(uint)mode),0);}catch{handle.Dispose();throw;}
    }
    static byte[] Header(uint key,uint flags=0)
    {byte[] h=new byte[16];BinaryPrimitives.WriteUInt32LittleEndian(h,1);BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4),key);BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8),flags);return h;}
    void Key(uint key){if(key>=capacity)throw new ArgumentOutOfRangeException(nameof(key));}
    byte[] Call(uint code,byte[] input,int outputSize)
    {
        lock(gate){ObjectDisposedException.ThrowIf(disposed,this);byte[] output=new byte[outputSize];
            if(!DeviceIoControl(handle,code,input,(uint)input.Length,output,(uint)output.Length,out uint returned,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),$"ElasticMemStore IOCTL 0x{code:X8} failed");
            if(returned!=outputSize)throw new InvalidDataException("Driver ABI output length mismatch");return output;}
    }
    public void Put(uint key,ReadOnlySpan<byte> page)
    {Key(key);if(page.Length!=PageBytes)throw new ArgumentException("Exact 4096-byte page required",nameof(page));byte[] b=new byte[4112];Header(key).CopyTo(b,0);page.CopyTo(b.AsSpan(16));try{Call(PutCode,b,0);}finally{Array.Clear(b);}}
    public KernelPagerReceipt PutCompressed(uint key,ReadOnlySpan<byte> page)
    {
        Key(key);if(page.Length!=PageBytes)throw new ArgumentException("Exact 4096-byte page required",nameof(page));
        byte[] b=new byte[4112];Header(key).CopyTo(b,0);page.CopyTo(b.AsSpan(16));
        try {
            byte[] o=Call(PutCompressedCode,b,32);
            uint U(int i)=>BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(i));
            ulong generation=BinaryPrimitives.ReadUInt64LittleEndian(o.AsSpan(8));
            if(U(0)!=1||U(4)!=key||generation==0||U(16)==0||U(20)!=U(16)+24||U(20)>=PageBytes||U(28)!=0)throw new InvalidDataException("Invalid compressed-page receipt; original backing MUST be retained");
            return new(key,generation,U(16),U(20),U(24));
        }finally{Array.Clear(b);}
    }
    byte[] Versioned(uint key,ulong generation)
    {Key(key);if(generation==0)throw new ArgumentOutOfRangeException(nameof(generation));byte[] b=new byte[24];Header(key).CopyTo(b,0);BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16),generation);return b;}
    public byte[] GetVersioned(uint key,ulong generation)=>Call(GetVersionedCode,Versioned(key,generation),PageBytes);
    public bool DeleteVersioned(uint key,ulong generation)
    {try{Call(DeleteVersionedCode,Versioned(key,generation),0);return true;}catch(Win32Exception ex)when(ex.NativeErrorCode==1168){return false;}}
    public byte[] Get(uint key){Key(key);return Call(GetCode,Header(key),PageBytes);}
    public bool Delete(uint key){Key(key);try{Call(DeleteCode,Header(key),0);return true;}catch(Win32Exception ex)when(ex.NativeErrorCode==1168){return false;}}
    public KernelPagerCapabilities Capabilities()
    {
        byte[] b=Call(CapabilitiesCode,Header(0),48);
        uint U(int i)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));ulong L(int i)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
        if(U(0)!=1||(U(4)&7)!=7||U(8)!=PageBytes||U(12)!=MaxPages||U(16)<MaxPages||U(16)>MaximumCapacity||(U(20)==0||U(20)>8)||U(24)!=0||U(28)!=0||(L(32)<32768||L(32)>32UL*1024*1024)||(L(40)==0||L(40)>64UL*1024*1024))throw new InvalidDataException("Unsupported SYS pager capabilities; no fallback");
        return new(U(0),U(4),U(8),U(12),U(16),U(20),L(32),L(40));
    }
    public void ConfigurePager(uint pages,ulong quotaBytes)
    {
        lock(gate)
        {
            var caps=Capabilities();if(pages==0||pages>caps.MaxCapacity||quotaBytes<(ulong)pages*8||quotaBytes>caps.MaxSessionQuotaBytes)throw new ArgumentOutOfRangeException(nameof(pages));
            byte[] b=new byte[32];Header(0).CopyTo(b,0);BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16),pages);BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24),quotaBytes);
            Call(ConfigureStoreCode,b,0);capacity=pages;
            if(Stats().MaxPages!=pages)throw new InvalidDataException("SYS capacity not confirmed");
        }
    }
    public void Reset()=>Call(ResetCode,Header(0),0);
    public KernelStats Stats()
    {
        byte[] b=Call(StatsCode,Header(0),104);
        uint U(int i)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));ulong L(int i)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
        if(U(0)!=1||U(12)!=capacity)throw new InvalidDataException("Unexpected driver ABI");
        return new(U(0),(StoreMode)U(4),U(8),U(12),L(16),L(24),L(32),L(40),L(48),L(56),L(64),L(72),L(80),L(88),L(96));
    }
    internal int RejectProbe(uint code,byte[] input,int outputSize)
    {lock(gate){ObjectDisposedException.ThrowIf(disposed,this);byte[] output=new byte[outputSize];return DeviceIoControl(handle,code,input,(uint)input.Length,output,(uint)output.Length,out _,IntPtr.Zero)?0:Marshal.GetLastWin32Error();}}
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;handle.Dispose();}}
}
public sealed record KernelPagerReceipt(uint Key,ulong Generation,uint PayloadBytes,uint AllocationBytes,uint Crc32);
public sealed record KernelStats(uint Version,KernelPageStore.StoreMode Mode,uint Pages,uint MaxPages,ulong LogicalBytes,ulong PayloadBytes,ulong SessionAllocationBytes,ulong SessionQuotaBytes,ulong GlobalAllocationBytes,ulong GlobalLimitBytes,ulong Puts,ulong Gets,ulong Deletes,ulong CompressedPages,ulong RawPages);

public sealed record KernelPagerCapabilities(uint Version,uint Features,uint PageBytes,uint DefaultCapacity,uint MaxCapacity,uint MaxSessions,ulong MaxSessionQuotaBytes,ulong GlobalLimitBytes);
