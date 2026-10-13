// Windows-only G3 microbenchmark. No paging integration, no compression claims.
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include <intrin.h>
using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
void check(HRESULT hr, const char* msg) { if (FAILED(hr)) throw std::runtime_error(std::string(msg)+": HRESULT="+std::to_string(static_cast<unsigned long>(hr))); }
double us(Clock::time_point a, Clock::time_point b) { return std::chrono::duration<double,std::micro>(b-a).count(); }
struct Handle { HANDLE h=INVALID_HANDLE_VALUE; ~Handle(){ if(h!=INVALID_HANDLE_VALUE && h) CloseHandle(h); } };
struct Memory { void* p=nullptr; explicit Memory(size_t n){p=VirtualAlloc(nullptr,n,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE); if(!p) throw std::runtime_error("VirtualAlloc failed");} ~Memory(){if(p)VirtualFree(p,0,MEM_RELEASE);} };
ComPtr<ID3D12Resource> buffer(ID3D12Device* device, D3D12_HEAP_TYPE type, size_t bytes, D3D12_RESOURCE_STATES state){
    D3D12_HEAP_PROPERTIES heap={}; heap.Type=type; heap.CreationNodeMask=heap.VisibleNodeMask=1;
    D3D12_RESOURCE_DESC desc={}; desc.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER; desc.Width=bytes; desc.Height=1;
    desc.DepthOrArraySize=1; desc.MipLevels=1; desc.SampleDesc.Count=1; desc.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ComPtr<ID3D12Resource> result; check(device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&desc,state,nullptr,IID_PPV_ARGS(&result)),"CreateCommittedResource"); return result;
}
int wmain(int argc, wchar_t** argv){
    if(argc<3){std::cerr<<"Usage: vram_bench.exe OUTPUT_DIR NEW_SCRATCH_FILE [iterations=100] [pool_mib=32] [adapter_index=0] [fault=none|skip-copy|wrong-offset] [fault_bytes=4096]\n";return 2;}
    std::wstring out=argv[1], diskPath=argv[2];
    try {
        unsigned iterations=argc>3?std::stoul(argv[3]):100, poolMiB=argc>4?std::stoul(argv[4]):32, adapterIndex=argc>5?std::stoul(argv[5]):0;
        if(iterations<10 || iterations>100000 || poolMiB<16 || poolMiB>256) throw std::runtime_error("iterations 10..100000; pool 16..256 MiB");
        const size_t pool=size_t(poolMiB)*1024*1024;
        std::wstring fault=argc>6?argv[6]:L"none"; size_t faultBytes=argc>7?std::stoull(argv[7]):4096;
        if(fault!=L"none" && fault!=L"skip-copy" && fault!=L"wrong-offset")throw std::runtime_error("Unknown fault injection");
        if(fault!=L"none" && faultBytes!=4096 && faultBytes!=65536 && faultBytes!=1048576 && faultBytes!=16777216)throw std::runtime_error("Unknown fault size");
        if(fault==L"wrong-offset" && pool<2*faultBytes)throw std::runtime_error("Fault test needs at least two regions");
        ComPtr<IDXGIFactory6> factory; check(CreateDXGIFactory2(0,IID_PPV_ARGS(&factory)),"CreateDXGIFactory2");
        ComPtr<IDXGIAdapter1> adapter; check(factory->EnumAdapterByGpuPreference(adapterIndex,DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,IID_PPV_ARGS(&adapter)),"EnumAdapterByGpuPreference");
        DXGI_ADAPTER_DESC1 ad={}; check(adapter->GetDesc1(&ad),"GetDesc1");
        if(ad.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) throw std::runtime_error("Software adapter rejected");
        ComPtr<ID3D12Device> device; check(D3D12CreateDevice(adapter.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&device)),"D3D12CreateDevice");
        D3D12_FEATURE_DATA_ARCHITECTURE arch={}; check(device->CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE,&arch,sizeof(arch)),"Architecture query");
        D3D12_FEATURE_DATA_D3D12_OPTIONS16 opts={}; HRESULT supported=device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16,&opts,sizeof(opts));
        if(arch.UMA || FAILED(supported) || !opts.GPUUploadHeapSupported){
            std::ofstream f(std::filesystem::path(out+L"\\status.json")); f<<"{\"status\":\"unsupported\",\"reason\":\"Discrete GPU with GPUUploadHeapSupported required; no fallback\"}\n";return 3;
        }
        ComPtr<IDXGIAdapter3> a3; check(adapter.As(&a3),"Adapter3"); DXGI_QUERY_VIDEO_MEMORY_INFO initialBudget={};
        check(a3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&initialBudget),"VRAM budget query");
        if(initialBudget.Budget<=initialBudget.CurrentUsage || pool > (initialBudget.Budget-initialBudget.CurrentUsage)/4) throw std::runtime_error("Pool exceeds 25% of available DXGI budget; refusing allocation");
        std::ofstream info(std::filesystem::path(out+L"\\device.json"));
        info<<"{\"schema\":\"elasticmem.g3.device.v1\",\"vendor_id\":"<<ad.VendorId<<",\"device_id\":"<<ad.DeviceId
            <<",\"adapter_index\":"<<adapterIndex<<",\"luid_low\":"<<ad.AdapterLuid.LowPart<<",\"luid_high\":"<<ad.AdapterLuid.HighPart
            <<",\"dedicated_video_bytes\":"<<ad.DedicatedVideoMemory<<",\"budget_bytes\":"<<initialBudget.Budget<<",\"usage_bytes\":"<<initialBudget.CurrentUsage
            <<",\"gpu_upload_supported\":true,\"uma\":false,\"pool_bytes\":"<<pool<<",\"iterations\":"<<iterations<<"}\n"; info.close();
        ComPtr<ID3D12CommandQueue> queue; D3D12_COMMAND_QUEUE_DESC q={}; q.Type=D3D12_COMMAND_LIST_TYPE_COPY;
        check(device->CreateCommandQueue(&q,IID_PPV_ARGS(&queue)),"CreateCommandQueue");
        ComPtr<ID3D12CommandAllocator> allocator; check(device->CreateCommandAllocator(q.Type,IID_PPV_ARGS(&allocator)),"CreateCommandAllocator");
        ComPtr<ID3D12GraphicsCommandList> list; check(device->CreateCommandList(0,q.Type,allocator.Get(),nullptr,IID_PPV_ARGS(&list)),"CreateCommandList"); check(list->Close(),"Initial Close");
        ComPtr<ID3D12Fence> fence; check(device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence)),"CreateFence");
        Handle event; event.h=CreateEventW(nullptr,FALSE,FALSE,nullptr); if(!event.h)throw std::runtime_error("CreateEvent"); uint64_t fenceValue=0;
        auto upload=buffer(device.Get(),D3D12_HEAP_TYPE_GPU_UPLOAD,pool,D3D12_RESOURCE_STATE_GENERIC_READ);
        auto readback=buffer(device.Get(),D3D12_HEAP_TYPE_READBACK,16*1024*1024,D3D12_RESOURCE_STATE_COPY_DEST);
        void* gpu=nullptr; D3D12_RANGE noRead={0,0}; check(upload->Map(0,&noRead,&gpu),"Map GPU_UPLOAD");
        Memory source(pool), materialized(16*1024*1024), io(16*1024*1024), expectedGpu(16*1024*1024);
        auto src=static_cast<uint8_t*>(source.p); uint64_t rng=0x8e11367aba7319ULL;
        for(size_t i=0;i<pool;++i){rng^=rng<<13; rng^=rng>>7; rng^=rng<<17; src[i]=static_cast<uint8_t>(rng);}
        std::memset(materialized.p,0,16*1024*1024); std::memset(io.p,0,16*1024*1024); std::memset(expectedGpu.p,0,16*1024*1024);
        std::memcpy(gpu,source.p,pool); _mm_sfence(); // CPU write-only; never inspect GPU_UPLOAD directly.
        Handle disk; disk.h=CreateFileW(diskPath.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,CREATE_NEW,
            FILE_ATTRIBUTE_TEMPORARY|FILE_FLAG_DELETE_ON_CLOSE|FILE_FLAG_NO_BUFFERING|FILE_FLAG_WRITE_THROUGH,nullptr);
        if(disk.h==INVALID_HANDLE_VALUE) throw std::runtime_error("Scratch CreateFile CREATE_NEW failed; supply a new filename");
        FILE_STORAGE_INFO storage={}; if(!GetFileInformationByHandleEx(disk.h,FileStorageInfo,&storage,sizeof(storage)))throw std::runtime_error("FileStorageInfo failed");
        unsigned sector=std::max(storage.LogicalBytesPerSector,storage.PhysicalBytesPerSectorForPerformance);
        if(!sector || 4096%sector) throw std::runtime_error("4 KiB not compatible with disk sector; abort rather than buffered fallback");
        // Use aligned VirtualAlloc memory. Populate disk once, outside measured reads.
        for(size_t offset=0;offset<pool;offset+=16*1024*1024){ DWORD n=static_cast<DWORD>(std::min<size_t>(16*1024*1024,pool-offset)), done=0;
            if(!WriteFile(disk.h,src+offset,n,&done,nullptr)||done!=n)throw std::runtime_error("Scratch initialization write failed"); }
        if(!FlushFileBuffers(disk.h))throw std::runtime_error("FlushFileBuffers failed");
        std::ofstream rows(std::filesystem::path(out+L"\\samples.csv")); rows<<"mode,bytes,iteration,warmup,total_us,transfer_us,materialize_us,byte_match\n";
        if(!rows)throw std::runtime_error("Cannot open samples.csv");
        for(size_t bytes : {size_t(4096),size_t(65536),size_t(1048576),size_t(16777216)}){
            for(unsigned rep=0;rep<iterations+10;++rep){
                size_t offset=(size_t(rep)%(pool/bytes))*bytes; // rotates within bounded pool
                std::memcpy(expectedGpu.p,src+offset,bytes); uint64_t stamp=(uint64_t(bytes)<<32)|rep; std::memcpy(expectedGpu.p,&stamp,sizeof(stamp));
                auto begin=Clock::now(); std::memcpy(static_cast<uint8_t*>(gpu)+offset,expectedGpu.p,bytes); _mm_sfence(); auto stop=Clock::now();
                double writeUs=us(begin,stop); // Report byte_match only after fenced restore validates this generation.
                check(allocator->Reset(),"Allocator reset"); check(list->Reset(allocator.Get(),nullptr),"List reset");
                bool inject=rep==10 && bytes==faultBytes && fault!=L"none";
                size_t copyOffset=inject && fault==L"wrong-offset" ? (offset+bytes)%pool : offset;
                if(!(inject && fault==L"skip-copy")) list->CopyBufferRegion(readback.Get(),0,upload.Get(),copyOffset,bytes);
                check(list->Close(),"Close copy"); ID3D12CommandList* lists[]={list.Get()};
                // Time submission+fence wait+Map+RAM materialization; command recording outside measured window.
                begin=Clock::now(); queue->ExecuteCommandLists(1,lists); check(queue->Signal(fence.Get(),++fenceValue),"Signal");
                if(fence->GetCompletedValue()==UINT64_MAX){check(device->GetDeviceRemovedReason(),"GPU device removed");throw std::runtime_error("GPU fence removal sentinel");}
                if(fence->GetCompletedValue()<fenceValue){ check(fence->SetEventOnCompletion(fenceValue,event.h),"SetEventOnCompletion");
                    if(WaitForSingleObject(event.h,10000)!=WAIT_OBJECT_0)throw std::runtime_error("GPU fence timeout"); }
                if(fence->GetCompletedValue()==UINT64_MAX){check(device->GetDeviceRemovedReason(),"GPU device removed after wait");throw std::runtime_error("GPU fence removal sentinel");}
                auto transferEnd=Clock::now(); void* mapped=nullptr; D3D12_RANGE readRange={0,bytes}; check(readback->Map(0,&readRange,&mapped),"Map readback");
                std::memcpy(materialized.p,mapped,bytes); readback->Unmap(0,&noRead); stop=Clock::now();
                bool matches=std::memcmp(materialized.p,expectedGpu.p,bytes)==0;
                rows<<"gpu_restore,"<<bytes<<","<<rep<<","<<(rep<10)<<","<<us(begin,stop)<<","<<us(begin,transferEnd)<<","<<us(transferEnd,stop)<<","<<matches<<"\n";
                if(!matches)throw std::runtime_error("GPU restore byte mismatch");
                rows<<"gpu_write,"<<bytes<<","<<rep<<","<<(rep<10)<<","<<writeUs<<","<<writeUs<<",0,1\n";
                LARGE_INTEGER at; at.QuadPart=offset; if(!SetFilePointerEx(disk.h,at,nullptr,FILE_BEGIN))throw std::runtime_error("SetFilePointerEx failed");
                DWORD done=0; begin=Clock::now(); if(!ReadFile(disk.h,io.p,static_cast<DWORD>(bytes),&done,nullptr)||done!=bytes)throw std::runtime_error("Unbuffered ReadFile failed");
                transferEnd=Clock::now(); std::memcpy(materialized.p,io.p,bytes); stop=Clock::now(); matches=std::memcmp(materialized.p,src+offset,bytes)==0;
                rows<<"disk_restore,"<<bytes<<","<<rep<<","<<(rep<10)<<","<<us(begin,stop)<<","<<us(begin,transferEnd)<<","<<us(transferEnd,stop)<<","<<matches<<"\n";
                if(!matches)throw std::runtime_error("Disk restore byte mismatch");
            }
        }
        upload->Unmap(0,nullptr); rows.close();
        DXGI_QUERY_VIDEO_MEMORY_INFO finalBudget={}; check(a3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&finalBudget),"Final VRAM budget query");
        std::ofstream status(std::filesystem::path(out+L"\\status.json")); status<<"{\"status\":\"measured\",\"byte_checks\":\"passed\",\"final_budget_bytes\":"<<finalBudget.Budget
            <<",\"final_usage_bytes\":"<<finalBudget.CurrentUsage<<",\"disk_sector_bytes\":"<<sector
            <<",\"limitations\":[\"Disk file I/O is not an NVMe-pagefile fault\",\"No paging or compression integration\",\"Repeated reads can hit device cache\",\"Fixed mode order, no concurrent GPU load\",\"Command recording and allocations excluded from GPU latency\",\"Small transfer batches do not characterize multi-GB capacity\"]}\n";
        return 0;
    } catch(const std::exception& e){ std::cerr<<e.what()<<"\n"; std::ofstream f(std::filesystem::path(out+L"\\status.json")); f<<"{\"status\":\"failed\",\"reason\":\"See stderr.txt; partial rows are not valid results\"}\n";return 1; }
}
