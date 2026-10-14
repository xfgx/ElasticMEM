namespace ElasticMem.Core.Backend.Vmm;

// Custom VMM memory-access-exit hook, NOT installed into Hyper-V managed VMs.
// Native dispatcher must have stopped ALL vCPUs/device DMA before allowing guest continuation.
public sealed class VmMemoryExitRouter
{
    readonly VmPageProvider provider;readonly ulong first,bytes;
    public VmMemoryExitRouter(VmPageProvider provider,ulong guestBase,int pages)
    {
        if(pages is <1 or >4096||guestBase%4096!=0)throw new ArgumentOutOfRangeException(nameof(pages));
        this.provider=provider??throw new ArgumentNullException(nameof(provider));if(pages!=provider.PageCount)throw new ArgumentException("Router/provider page-count mismatch");first=guestBase;bytes=(ulong)pages*4096;_=checked(first+bytes);
    }
    public bool RestoreForUnmappedGpa(ulong guestPhysicalAddress)
    {
        if(guestPhysicalAddress<first||guestPhysicalAddress-first>=bytes)return false; // MMIO/unowned GPA belongs to another dispatcher.
        int page=(int)((guestPhysicalAddress-first)/4096);var state=provider.State(page);
        if(state==VmPageState.FailedPaused)throw new InvalidOperationException("Failed pager page: guest MUST remain paused");
        if(state!=VmPageState.Stored)return false;
        provider.RestoreBeforeGuestResume(page);return true; // Caller retries WHvRunVirtualProcessor only AFTER successful restore.
    }
}
