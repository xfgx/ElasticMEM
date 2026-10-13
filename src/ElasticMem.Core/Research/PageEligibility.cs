namespace ElasticMem.Core.Research;
/// <summary>Pure classification of x64 QueryWorkingSetEx flags; invalid flags are never interpreted as ShareCount.</summary>
public readonly record struct PageResidency(bool Valid, bool? Shareable, int? ShareCount, bool? LargePage, ulong Flags)
{
    public static PageResidency Decode(ulong flags) => (flags & 1) == 0
        ? new(false, null, null, null, flags)
        : new(true, (flags & (1UL << 15)) != 0, (int)((flags >> 1) & 7), (flags & (1UL << 23)) != 0, flags);
}
public static class PageEligibility
{
    public const uint Private = 0x20000, Mapped = 0x40000, Image = 0x1000000;
    public static string RegionName(uint type) => type switch { Private => "MEM_PRIVATE", Mapped => "MEM_MAPPED", Image => "MEM_IMAGE", _ => "OTHER" };
    public static string Classify(uint type, PageResidency state) => !state.Valid ? "not_valid_in_working_set"
        : type == Image ? "resident_image" : type == Mapped ? "resident_mapped" : type != Private ? "resident_other"
        : state.Shareable == true || state.ShareCount > 1 ? "resident_private_shareable_or_shared" : "resident_private_exclusive";
    public static bool Eligible(uint type, PageResidency state) => Classify(type, state) == "resident_private_exclusive";
}

/// <summary>VirtualQueryEx may return a tail beginning at the requested page, not the original region base.
/// Match allocation base and containment, never equality of returned region bases.
/// This does not detect free/reallocation at the same address between calls.</summary>
public static class PageMapping
{
    public static bool Contains(ulong regionBase,ulong regionSize,ulong pageAddress,ulong bytes)
        => bytes>0 && regionSize>0 && pageAddress>=regionBase && pageAddress-regionBase<regionSize
           && bytes<=regionSize-(pageAddress-regionBase);
    public static bool SameAllocationRange(ulong pageAddress,ulong bytes,ulong originalBase,ulong originalSize,
        ulong originalAllocation,ulong queriedBase,ulong queriedSize,ulong queriedAllocation)
        => originalAllocation==queriedAllocation && Contains(originalBase,originalSize,pageAddress,bytes)
           && Contains(queriedBase,queriedSize,pageAddress,bytes);
}
