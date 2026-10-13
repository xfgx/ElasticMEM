using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ElasticMem.Core.Research;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Direct3D;
using DRange=Vortice.Direct3D12.Range;
namespace ElasticMem.App.Research;
internal static unsafe class GpuResearch
{
    [DllImport("kernel32",SetLastError=true)]static extern IntPtr VirtualAlloc(IntPtr p,nuint size,uint type,uint protect);
    [DllImport("kernel32")]static extern bool VirtualFree(IntPtr p,nuint size,uint type);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr sec,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32",SetLastError=true)]static extern bool WriteFile(SafeFileHandle h,IntPtr p,uint n,out uint done,IntPtr overlapped);
    [DllImport("kernel32",SetLastError=true)]static extern bool ReadFile(SafeFileHandle h,IntPtr p,uint n,out uint done,IntPtr overlapped);
    [DllImport("kernel32",SetLastError=true)]static extern bool SetFilePointerEx(SafeFileHandle h,long offset,out long pos,uint method);
    [DllImport("kernel32",SetLastError=true)]static extern bool FlushFileBuffers(SafeFileHandle h);
    sealed class Ram:IDisposable{public IntPtr Pointer;public int Size;public Ram(int size){Size=size;Pointer=VirtualAlloc(IntPtr.Zero,(nuint)size,0x3000,4);if(Pointer==IntPtr.Zero)throw new OutOfMemoryException();}public Span<byte> Span=>new((void*)Pointer,Size);public void Dispose(){if(Pointer!=IntPtr.Zero){VirtualFree(Pointer,0,0x8000);Pointer=IntPtr.Zero;}}}
    static bool _gpuQuarantined;
    static readonly List<IDisposable> Quarantine=new();
    sealed class GpuLifetime:IDisposable
    {
        readonly List<IDisposable> _objects=new();public ID3D12Fence? Fence;public ulong Target;
        public T Add<T>(T value)where T:IDisposable{_objects.Add(value);return value;}
        public bool Pending=>Fence!=null&&Target>0&&Fence.CompletedValue!=ulong.MaxValue&&Fence.CompletedValue<Target;
        public void Dispose(){if(Pending){_gpuQuarantined=true;Quarantine.AddRange(_objects);return;}for(int i=_objects.Count-1;i>=0;i--)_objects[i].Dispose();}
    }
    public static object Probe()
    {
        using var factory=DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);var adapters=new List<object>();
        for(uint i=0;i<16;i++)
        {
            var hr=factory.EnumAdapterByGpuPreference<IDXGIAdapter1>(i,GpuPreference.HighPerformance,out var adapter);if(hr.Failure||adapter is null)break;
            using(adapter){try{using var device=D3D12.D3D12CreateDevice<ID3D12Device>(adapter,FeatureLevel.Level_12_0);var supported=device.CheckFeatureSupport<FeatureDataD3D12Options16>(Vortice.Direct3D12.Feature.Options16);var architecture=device.CheckFeatureSupport<FeatureDataArchitecture>(Vortice.Direct3D12.Feature.Architecture);
                using var a3=adapter.QueryInterface<IDXGIAdapter3>();var budget=a3.QueryVideoMemoryInfo(0,MemorySegmentGroup.Local);
                adapters.Add(new{index=i,name=adapter.Description1.Description,software=(adapter.Description1.Flags&AdapterFlags.Software)!=0,uma=(bool)architecture.Uma,gpu_upload_supported=(bool)supported.GPUUploadHeapSupported,budget_bytes=budget.Budget,usage_bytes=budget.CurrentUsage});}
                catch(Exception ex){adapters.Add(new{index=i,name=adapter.Description1.Description,error=ex.Message});}}
        }
        return new{schema="elasticmem.g3.probe.v1",adapters,limitation="Capability probe, not a performance measurement"};
    }
    public static object Run(ResearchRun run,string scratchDirectory,int iterations,int poolMiB,int adapterIndex,bool negative,IProgress<string>? progress,CancellationToken ct)
    {
        if(_gpuQuarantined)throw new NotSupportedException("Prior GPU timeout: resources quarantined, restart the app before another GPU test");
        using var lifetime=new GpuLifetime();
        if(iterations<10||iterations>2000||poolMiB<16||poolMiB>128||adapterIndex<0)throw new ArgumentException("Invalid G3 configuration");
        if(negative&&poolMiB<32)throw new ArgumentException("Negative checks need at least 32 MiB pool");
        if(!Directory.Exists(scratchDirectory))throw new DirectoryNotFoundException("Select an existing local SSD directory");
        var factory=lifetime.Add(DXGI.CreateDXGIFactory2<IDXGIFactory6>(false));var adapter=lifetime.Add(factory.EnumAdapterByGpuPreference<IDXGIAdapter1>((uint)adapterIndex,GpuPreference.HighPerformance));
        if((adapter.Description1.Flags&AdapterFlags.Software)!=0)throw new NotSupportedException("Software GPU not allowed");
        var device=lifetime.Add(D3D12.D3D12CreateDevice<ID3D12Device>(adapter,FeatureLevel.Level_12_0));
        var options=device.CheckFeatureSupport<FeatureDataD3D12Options16>(Vortice.Direct3D12.Feature.Options16);var arch=device.CheckFeatureSupport<FeatureDataArchitecture>(Vortice.Direct3D12.Feature.Architecture);
        if(!options.GPUUploadHeapSupported||arch.Uma)throw new NotSupportedException("Discrete GPU with GPU_UPLOAD support required; no fallback");
        var a3=lifetime.Add(adapter.QueryInterface<IDXGIAdapter3>());var budget=a3.QueryVideoMemoryInfo(0,MemorySegmentGroup.Local);int pool=poolMiB<<20;
        if(budget.Budget<=budget.CurrentUsage||(ulong)pool>(budget.Budget-budget.CurrentUsage)/4)throw new NotSupportedException("Pool exceeds 25% of currently available DXGI budget");
        run.WriteJson("gpu-device.json",new{index=adapterIndex,adapter=adapter.Description1.Description,vendor=adapter.Description1.VendorId,device=adapter.Description1.DeviceId,pool,budget=budget.Budget,usage=budget.CurrentUsage,gpu_upload=true});
        var queue=lifetime.Add(device.CreateCommandQueue(CommandListType.Copy));var allocator=lifetime.Add(device.CreateCommandAllocator(CommandListType.Copy));var list=lifetime.Add(device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Copy,allocator));list.Close();
        var fence=lifetime.Add(device.CreateFence());lifetime.Fence=fence;var ev=lifetime.Add(new AutoResetEvent(false));ulong seq=0;
        var upload=lifetime.Add(device.CreateCommittedResource(HeapType.GpuUpload,ResourceDescription.Buffer((ulong)pool),ResourceStates.GenericRead));
        var readback=lifetime.Add(device.CreateCommittedResource(HeapType.Readback,ResourceDescription.Buffer(16UL<<20),ResourceStates.CopyDest));
        IntPtr gpu=IntPtr.Zero;upload.Map(0,new DRange(0,0),&gpu).CheckError();
        try
        {
            using var source=new Ram(pool);using var expected=new Ram(16<<20);using var materialized=new Ram(16<<20);using var io=new Ram(16<<20);
            ulong rng=0x8e11367aba7319UL;for(int i=0;i<pool;i++){rng^=rng<<13;rng^=rng>>7;rng^=rng<<17;source.Span[i]=(byte)rng;}
            materialized.Span.Clear();io.Span.Clear();source.Span.CopyTo(new Span<byte>((void*)gpu,pool));if(System.Runtime.Intrinsics.X86.Sse.IsSupported)System.Runtime.Intrinsics.X86.Sse.StoreFence();else Thread.MemoryBarrier();
            string scratch=Path.Combine(scratchDirectory,"ElasticMem_"+Guid.NewGuid().ToString("N")+".tmp");
            using var disk=CreateFile(scratch,0xC0000000,0,IntPtr.Zero,1,0x04000000|0x20000000|0x80000000|0x100,IntPtr.Zero);
            if(disk.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Unbuffered scratch CREATE_NEW failed");
            for(int off=0;off<pool;off+=16<<20){uint n=(uint)Math.Min(16<<20,pool-off);if(!WriteFile(disk,source.Pointer+off,n,out var done,IntPtr.Zero)||done!=n)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Aligned write failed (check device sector size)");}
            if(!FlushFileBuffers(disk))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var csv=new StreamWriter(run.FilePath("gpu-samples.csv"));csv.WriteLine("mode,bytes,iteration,warmup,total_us,handler_us,byte_match");var samples=new List<(string Mode,int Bytes,double Us)>();int detected=0;
            foreach(int bytes in new[]{4096,65536,1048576,16777216})
            {
                progress?.Report($"G3: {bytes/1024} KiB · GPU и диск");
                for(int rep=0;rep<iterations+10;rep++)
                {
                    ct.ThrowIfCancellationRequested();int offset=rep%(pool/bytes)*bytes;source.Span.Slice(offset,bytes).CopyTo(expected.Span);BitConverter.TryWriteBytes(expected.Span,((ulong)bytes<<32)|(uint)rep);
                    long t=Stopwatch.GetTimestamp();expected.Span[..bytes].CopyTo(new Span<byte>((void*)(gpu+offset),bytes));if(System.Runtime.Intrinsics.X86.Sse.IsSupported)System.Runtime.Intrinsics.X86.Sse.StoreFence();else Thread.MemoryBarrier();double writeUs=Elapsed(t);
                    // Negative checks exercise stale and wrong-offset copies independently per size.
                    for(int fault=0;fault<(negative&&rep==10?3:1);fault++)
                    {
                        if(fault==2&&pool<2*bytes)continue;
                        if(fault==1){expected.Span[0]^=0x5A;expected.Span[..bytes].CopyTo(new Span<byte>((void*)(gpu+offset),bytes));if(System.Runtime.Intrinsics.X86.Sse.IsSupported)System.Runtime.Intrinsics.X86.Sse.StoreFence();else Thread.MemoryBarrier();}
                        long handler=Stopwatch.GetTimestamp();allocator.Reset();list.Reset(allocator);
                        if(fault!=1)list.CopyBufferRegion(readback,0,upload,(ulong)(fault==2?(offset+bytes)%pool:offset),(ulong)bytes);
                        list.Close();t=Stopwatch.GetTimestamp();lifetime.Target=seq+1;queue.ExecuteCommandList(list);queue.Signal(fence,++seq).CheckError();
                        if(fence.CompletedValue==ulong.MaxValue)throw new IOException("GPU device removed");
                        if(fence.CompletedValue<seq){fence.SetEventOnCompletion(seq,ev).CheckError();if(!ev.WaitOne(10000))throw new TimeoutException("GPU fence timeout");}
                        if(fence.CompletedValue==ulong.MaxValue)throw new IOException("GPU removed during wait");
                        IntPtr mapped=IntPtr.Zero;readback.Map(0,new DRange(0,(nuint)bytes),&mapped).CheckError();
                        try{new ReadOnlySpan<byte>((void*)mapped,bytes).CopyTo(materialized.Span);}finally{readback.Unmap(0,new DRange(0,0));}
                        double restoreUs=Elapsed(t),handlerUs=Elapsed(handler);bool matches=materialized.Span[..bytes].SequenceEqual(expected.Span[..bytes]);
                        if(fault!=0){run.Log($"Fault {fault}, {bytes} bytes: mismatch={!matches}");if(matches)throw new InvalidDataException("Negative copy check failed to detect fault");detected++;continue;}
                        if(!matches)throw new InvalidDataException("GPU byte mismatch");
                        csv.WriteLine(FormattableString.Invariant($"gpu_restore,{bytes},{rep},{(rep<10?1:0)},{restoreUs:F4},{handlerUs:F4},1"));
                        csv.WriteLine(FormattableString.Invariant($"gpu_write,{bytes},{rep},{(rep<10?1:0)},{writeUs:F4},{writeUs:F4},1"));
                        if(rep>=10){samples.Add(("gpu_restore",bytes,restoreUs));samples.Add(("gpu_write",bytes,writeUs));}
                    }
                    if(!SetFilePointerEx(disk,offset,out _,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());t=Stopwatch.GetTimestamp();
                    if(!ReadFile(disk,io.Pointer,(uint)bytes,out var read,IntPtr.Zero)||read!=bytes)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Aligned read failed");
                    io.Span[..bytes].CopyTo(materialized.Span);double diskUs=Elapsed(t);if(!materialized.Span[..bytes].SequenceEqual(source.Span.Slice(offset,bytes)))throw new InvalidDataException("Disk byte mismatch");
                    csv.WriteLine(FormattableString.Invariant($"disk_restore,{bytes},{rep},{(rep<10?1:0)},{diskUs:F4},{diskUs:F4},1"));if(rep>=10)samples.Add(("disk_restore",bytes,diskUs));
                }
                if(negative)
                {
                    if(!SetFilePointerEx(disk,0,out _,0)||!ReadFile(disk,io.Pointer,(uint)bytes,out var firstRead,IntPtr.Zero)||firstRead!=bytes)throw new IOException("Disk control read failed");
                    source.Span[0]^=0x5A;
                    if(!SetFilePointerEx(disk,0,out _,0)||!WriteFile(disk,source.Pointer,(uint)bytes,out var written,IntPtr.Zero)||written!=bytes||!FlushFileBuffers(disk))throw new IOException("Disk generation write failed");
                    if(io.Span[..bytes].SequenceEqual(source.Span[..bytes]))throw new InvalidDataException("Skipped disk read not detected");detected++;run.Log($"Disk skipped-read generation check {bytes}: mismatch detected");
                    if(!SetFilePointerEx(disk,0,out _,0)||!ReadFile(disk,io.Pointer,(uint)bytes,out var reread,IntPtr.Zero)||reread!=bytes||!io.Span[..bytes].SequenceEqual(source.Span[..bytes]))throw new InvalidDataException("Disk new-generation restore mismatch");
                }
                csv.Flush();
            }
            return new{schema="elasticmem.g3.app.v1",status="measured_microbenchmark_only",negative_faults_detected=detected,groups=samples.GroupBy(x=>(x.Mode,x.Bytes)).Select(g=>new{mode=g.Key.Mode,bytes=g.Key.Bytes,n=g.Count(),p50_us=Percentile(g.Select(x=>x.Us),.5),p95_us=Percentile(g.Select(x=>x.Us),.95),p99_us=Percentile(g.Select(x=>x.Us),.99)}),
                limitations=new[]{"OWN buffer storage only; no transparent foreign-process paging, VRAM migration or Windows pagefile integration","Disk comparator is unbuffered file I/O, not a measured NVMe-pagefile fault; path/device cache matter","submit+wait+RAM-copy latency; handler_us additionally includes command recording","Fixed GPU-then-disk order and repeated reads; device cache/power/loaded GPU not controlled","Pool is bounded microtest, not multi-GB capacity evidence"}};
        }
        finally{if(!lifetime.Pending)upload.Unmap(0,new DRange(0,(nuint)pool));else run.Log("GPU work still pending: resource disposal quarantined until process exit; restart required");}
    }
    static double Elapsed(long t)=>(Stopwatch.GetTimestamp()-t)*1_000_000.0/Stopwatch.Frequency;
    static double Percentile(IEnumerable<double> values,double q){var a=values.Order().ToArray();double p=(a.Length-1)*q;int lo=(int)p,hi=(int)Math.Ceiling(p);return a[lo]+(a[hi]-a[lo])*(p-lo);}
}
