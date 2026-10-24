using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ElasticMem.Vmm;
// Process fields cover ONLY the VMM process. System fields are host-wide and include kernel pool, so moving
// pages from the VMM into a driver pool cannot appear as a saving when system commit/in-use is compared.
public sealed record VmmSnapshot(string Stage,long WorkingSetBytes,long PrivateCommitBytes,long PeakWorkingSetBytes,ulong HostAvailablePhysicalBytes,ulong HostTotalPhysicalBytes,
    ulong SystemCommitBytes,ulong KernelPagedPoolBytes,ulong KernelNonpagedPoolBytes,ulong SystemCacheBytes,DateTimeOffset Time);
public static class VmmMetrics
{
    [StructLayout(LayoutKind.Sequential)]struct MemoryStatus{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [StructLayout(LayoutKind.Sequential)]struct PerformanceInformation{public uint Size;public nuint CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;public uint HandleCount,ProcessCount,ThreadCount;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="K32GetPerformanceInfo")][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetPerformanceInfo(out PerformanceInformation info,uint size);
    public static VmmSnapshot Capture(string stage)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var m=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref m))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(!GetPerformanceInfo(out var pi,(uint)Marshal.SizeOf<PerformanceInformation>()))throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong page=pi.PageSize;
        using var p=Process.GetCurrentProcess();p.Refresh();
        return new(stage,p.WorkingSet64,p.PrivateMemorySize64,p.PeakWorkingSet64,m.AvailablePhysical,m.TotalPhysical,(ulong)pi.CommitTotal*page,(ulong)pi.KernelPaged*page,(ulong)pi.KernelNonpaged*page,(ulong)pi.SystemCache*page,DateTimeOffset.UtcNow);
    }
}
