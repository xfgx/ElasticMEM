using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ElasticMem.Vmm;
public sealed record VmmSnapshot(string Stage,long WorkingSetBytes,long PrivateCommitBytes,long PeakWorkingSetBytes,ulong HostAvailablePhysicalBytes,ulong HostTotalPhysicalBytes,DateTimeOffset Time);
public static class VmmMetrics
{
    [StructLayout(LayoutKind.Sequential)]struct MemoryStatus{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static VmmSnapshot Capture(string stage)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var m=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref m))throw new Win32Exception(Marshal.GetLastWin32Error());
        using var p=Process.GetCurrentProcess();p.Refresh();return new(stage,p.WorkingSet64,p.PrivateMemorySize64,p.PeakWorkingSet64,m.AvailablePhysical,m.TotalPhysical,DateTimeOffset.UtcNow);
    }
}
