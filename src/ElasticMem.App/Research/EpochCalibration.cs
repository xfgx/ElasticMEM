using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static unsafe class EpochCalibration
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr VirtualAlloc(IntPtr address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll")]static extern bool VirtualFree(IntPtr address,nuint size,uint free);
    [DllImport("psapi.dll",SetLastError=true)]static extern bool EmptyWorkingSet(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]struct WS {public IntPtr Address;public UIntPtr Flags;}
    [DllImport("kernel32.dll",EntryPoint="K32QueryWorkingSetEx",SetLastError=true)]static extern bool QueryWorkingSet(IntPtr h,[In,Out]WS[] p,uint size);
    public static object Worker()
    {
        const int pages=8192;IntPtr memory=VirtualAlloc(IntPtr.Zero,(nuint)(pages*4096),0x3000,4);if(memory==IntPtr.Zero)throw new OutOfMemoryException();using var self=Process.GetCurrentProcess();var states=Enumerable.Range(0,pages).Select(i=>new WS{Address=memory+i*4096}).ToArray();var results=new List<object>();ulong checksum=0;
        try
        {
            for(int i=0;i<pages;i++)((ulong*)memory)[i*512]=(ulong)i+1;
            // Rotate phase order over three rounds to expose run-order drift.
            string[] phases={"baseline","trim","trim_query"};
            var rates=new Dictionary<string,List<double>>();foreach(string phase in phases)rates[phase]=new();
            for(int round=0;round<10;round++)for(int k=0;k<3;k++)
            {
                string phase=phases[(k+round)%3];long operations=0;var total=Stopwatch.StartNew();if(!Native.TryGetMemInfo(self.Handle,out var before))throw new IOException("Calibration pre-counters unavailable");TimeSpan cpuBefore=self.TotalProcessorTime;
                for(int epoch=0;epoch<2;epoch++)
                {
                    var timer=Stopwatch.StartNew();if(phase!="baseline"&&!EmptyWorkingSet(self.Handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if(phase=="trim_query"&&!QueryWorkingSet(self.Handle,states,(uint)(states.Length*Marshal.SizeOf<WS>())))throw new IOException("Calibration query failed");
                    while(timer.Elapsed.TotalSeconds<1){for(int i=0;i<512;i++){int page=(int)((uint)(operations+i)*2654435761U%(uint)pages);checksum+=Volatile.Read(ref ((ulong*)memory)[page*512]);}operations+=512;}
                    if(phase=="trim_query"&&!QueryWorkingSet(self.Handle,states,(uint)(states.Length*Marshal.SizeOf<WS>())))throw new IOException("Calibration post-query failed");
                }
                if(!Native.TryGetMemInfo(self.Handle,out var after))throw new IOException("Calibration post-counters unavailable");double elapsed=total.Elapsed.TotalSeconds;rates[phase].Add(operations/elapsed);results.Add(new{round,phase,operations,elapsed_ms=elapsed*1000,operations_per_second=operations/elapsed,cpu_ms=(self.TotalProcessorTime-cpuBefore).TotalMilliseconds,page_fault_delta=(long)after.PageFaultCount-before.PageFaultCount});
            }
            if(!EmptyWorkingSet(self.Handle)||!QueryWorkingSet(self.Handle,states,(uint)(states.Length*Marshal.SizeOf<WS>())))throw new IOException("Own fault probe trim/query failed");
            var touchTimes=new List<double>();int residentSkipped=0;
            if(!Native.TryGetMemInfo(self.Handle,out var faultsBefore))throw new IOException("Fault probe counters unavailable");
            for(int i=0;i<256;i++)
            {
                int page=i*pages/256;if((states[page].Flags.ToUInt64()&1)!=0){residentSkipped++;continue;}
                long t=Stopwatch.GetTimestamp();checksum+=Volatile.Read(ref ((ulong*)memory)[page*512]);touchTimes.Add((Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency);
            }
            if(!Native.TryGetMemInfo(self.Handle,out var faultsAfter))throw new IOException("Fault probe counters unavailable");
            return new{schema="elasticmem.g0.calibration.v2",status="controlled_own_child_workload_only",pages,allocated_bytes=pages*4096,rounds=10,epoch_seconds=1,phases=results,checksum,
                paired_intervals=new{trim=CalibrationStatistics.PairedInterval(rates["baseline"].ToArray(),rates["trim"].ToArray()),trim_query=CalibrationStatistics.PairedInterval(rates["baseline"].ToArray(),rates["trim_query"].ToArray())},
                own_post_trim_reads=new{observations=touchTimes.Count,resident_before_read_skipped=residentSkipped,p50_us=touchTimes.Count>0?(double?)CodecBench.Quantile(touchTimes,.5):null,p95_us=touchTimes.Count>0?(double?)CodecBench.Quantile(touchTimes,.95):null,page_fault_delta=(long)faultsAfter.PageFaultCount-faultsBefore.PageFaultCount,backing_origin="unknown_standby_compressed_or_pagefile",xpress_fault_latency_us=(double?)null,pagefile_fault_latency_us=(double?)null},
                limitations=new[]{"Own32MiB volatile page-read workload in isolated child; not browser throughput or real user task","Rotated phase order,each2seconds,ten paired rounds; descriptive bootstrap interval,not causal or zero-overhead proof","No pagefile/compression changes; soft vs hard faults not classified; do not extrapolate overhead to Chrome","Post-trim timed reads have unknown backing; Valid sampled before series,can change before each touch; do NOT relabel Xpress/pagefile latency","Measurement interference changes execution; no exact access trace or universal ceiling"}};
        }
        finally{VirtualFree(memory,0,0x8000);}
    }
    public static object Run(ResearchRun run,CancellationToken ct)
    {
        string target=run.FilePath("inputs/calibration-child.json");var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--research");start.ArgumentList.Add("g0-worker");start.ArgumentList.Add("--out");start.ArgumentList.Add(target);
        using var child=Process.Start(start)??throw new IOException("Cannot start calibration child");var timeout=Stopwatch.StartNew();
        try{while(!child.WaitForExit(100)){ct.ThrowIfCancellationRequested();if(timeout.Elapsed.TotalSeconds>180)throw new TimeoutException("Calibration child exceeded180seconds");}if(child.ExitCode!=0)throw new IOException("Calibration child failed; see private input report");using var doc=JsonDocument.Parse(File.ReadAllText(target));return doc.RootElement.Clone();}
        finally{if(!child.HasExited){child.Kill();child.WaitForExit(10000);}}
    }
}
