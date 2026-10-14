using System.Security.Cryptography;
namespace ElasticMem.Core.Backend.Vmm;

// VMM contracts, not foreign-process memory APIs. Caller must serialize CPU and DMA access.
public interface IVmPause : IDisposable { void KeepPausedOnFailure(); }
public interface IVmQuiescence { IVmPause PauseCpuAndIo(); bool IsEvictable(int page); }
public interface IVmPageBacking
{
    byte[] Read(int page);void Write(int page,byte[] bytes);
    void Unmap(int page);void Map(int page);void Decommit(int page);void Commit(int page);
}
public interface IVmCompressedStore
{void Put(uint key,byte[] bytes);byte[] Get(uint key);bool Delete(uint key);}
public enum VmPageState { Resident, Stored, FailedPaused }
public sealed record VmPageTransition(int Page,VmPageState State,long ReleasedBackingCommitBytes,bool CleanupPending);

public sealed class VmPageProvider
{
    readonly IVmPageBacking backing;readonly IVmCompressedStore store;readonly IVmQuiescence quiescence;
    readonly byte[]?[] hashes;readonly VmPageState[] states;readonly object gate=new();readonly bool[] cleanup;
    public VmPageProvider(int pages,IVmPageBacking backing,IVmCompressedStore store,IVmQuiescence quiescence)
    {
        if(pages is <1 or >4096)throw new ArgumentOutOfRangeException(nameof(pages));
        this.backing=backing??throw new ArgumentNullException(nameof(backing));this.store=store??throw new ArgumentNullException(nameof(store));this.quiescence=quiescence??throw new ArgumentNullException(nameof(quiescence));
        hashes=new byte[]?[pages];states=new VmPageState[pages];cleanup=new bool[pages];
    }
    public VmPageState State(int page){lock(gate){Validate(page);return states[page];}}
    void Validate(int page){if((uint)page>=(uint)states.Length)throw new ArgumentOutOfRangeException(nameof(page));}
    public VmPageTransition Evict(int page)
    {
        lock(gate)
        {
            Validate(page);if(states[page]!=VmPageState.Resident)throw new InvalidOperationException("Eviction requires a resident page");
            using var pause=quiescence.PauseCpuAndIo();
            if(!quiescence.IsEvictable(page))throw new InvalidOperationException("Pinned/DMA/ineligible page cannot be evicted");
            byte[] data=backing.Read(page);try
            {
                if(data.Length!=4096)throw new InvalidDataException("Exact VM page shape required");
                // Failure here leaves the original backing committed/mapped. Never discard first.
                store.Put((uint)page,data);
                hashes[page]=SHA256.HashData(data);
                bool unmapped=false;
                try{backing.Unmap(page);unmapped=true;backing.Decommit(page);}
                catch
                {
                    try{if(unmapped)backing.Map(page);try{store.Delete((uint)page);}catch{cleanup[page]=true;}}
                    catch{states[page]=VmPageState.FailedPaused;pause.KeepPausedOnFailure();}
                    throw;
                }
                states[page]=VmPageState.Stored;cleanup[page]=false;
                return new(page,states[page],4096,false);
            }
            finally{Array.Clear(data);}
        }
    }
    // VMM invokes this on an unmapped-GPA exit BEFORE continuing the guest, not a guest VEH hook.
    public VmPageTransition RestoreBeforeGuestResume(int page)
    {
        lock(gate)
        {
            Validate(page);if(states[page]!=VmPageState.Stored)throw new InvalidOperationException("Restore requires a stored page");
            using var pause=quiescence.PauseCpuAndIo();byte[]? data=null;
            try
            {
                data=store.Get((uint)page);if(data.Length!=4096)throw new InvalidDataException("VM restore length mismatch");
                if(hashes[page]==null||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data),hashes[page]!))throw new InvalidDataException("VM restore content mismatch");
                backing.Commit(page);backing.Write(page,data);backing.Map(page);
                states[page]=VmPageState.Resident;hashes[page]=null;
                try{cleanup[page]=!store.Delete((uint)page);}catch{cleanup[page]=true;}
                // A stale compressed copy is not authoritative while resident; overwrite on next eviction.
                return new(page,states[page],-4096,cleanup[page]);
            }
            catch{states[page]=VmPageState.FailedPaused;pause.KeepPausedOnFailure();throw;}
            finally{if(data!=null)Array.Clear(data);}
        }
    }
}
