using System.Runtime.InteropServices;
namespace ElasticMem.Vmm;
// x64 ABI subset only. WHP owns EPT/NPT; we change GPA mappings through its API,
// never patch Hyper-V-managed partitions or host page tables.
public static class WhpNative
{
    [StructLayout(LayoutKind.Explicit,Size=16)]
    public struct RegisterValue
    {
        [FieldOffset(0)] public ulong Value;
        [FieldOffset(8)] public uint Limit;
        [FieldOffset(12)] public ushort Selector;
        [FieldOffset(14)] public ushort Attributes;
        public static RegisterValue Scalar(ulong v)=>new(){Value=v};
        public static RegisterValue Segment(ushort selector,ushort attributes)=>new(){Value=(ulong)selector*16,Limit=0xffff,Selector=selector,Attributes=attributes};
    }
    [StructLayout(LayoutKind.Explicit,Size=224)]
    public struct ExitContext
    {
        [FieldOffset(0)] public uint Reason;
        [FieldOffset(32)] public ulong Rip;
        [FieldOffset(40)] public ulong Rflags;
        [FieldOffset(68)] public uint AccessInfo;
        [FieldOffset(72)] public ulong Gpa;
        [FieldOffset(80)] public ulong Gva;
        public bool GpaUnmapped=>(AccessInfo&4)!=0;
    }
    [DllImport("WinHvPlatform.dll")] public static extern int WHvGetCapability(uint code,out int value,uint size,out uint written);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvCreatePartition(out IntPtr partition);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvSetPartitionProperty(IntPtr partition,uint code,ref uint value,uint size);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvSetupPartition(IntPtr partition);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvDeletePartition(IntPtr partition);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvCreateVirtualProcessor(IntPtr partition,uint index,uint flags);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvDeleteVirtualProcessor(IntPtr partition,uint index);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvRunVirtualProcessor(IntPtr partition,uint index,out ExitContext exit,uint bytes);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvCancelRunVirtualProcessor(IntPtr partition,uint index,uint flags);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvSetVirtualProcessorRegisters(IntPtr partition,uint index,uint[] names,uint count,RegisterValue[] values);
    [DllImport("WinHvPlatform.dll")] public static extern int WHvGetVirtualProcessorRegisters(IntPtr partition,uint index,uint[] names,uint count,[Out] RegisterValue[] values);
    public static void Check(int result)=>Marshal.ThrowExceptionForHR(result);
    public static WhpAvailability Probe()
    {
        if(!OperatingSystem.IsWindows()||RuntimeInformation.ProcessArchitecture!=Architecture.X64)return new(false,"requires_windows_x64",null);
        try{int hr=WHvGetCapability(0,out int present,4,out uint written);if(hr<0)return new(false,"capability_failed",hr);return written==4&&present!=0?new(true,"hypervisor_present",null):new(false,"hypervisor_not_present",null);}
        catch(Exception ex)when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException){return new(false,"whp_api_unavailable",ex.HResult);}
    }
}
public sealed record WhpAvailability(bool Available,string Status,int? HResult);
