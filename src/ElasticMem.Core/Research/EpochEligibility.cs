namespace ElasticMem.Core.Research;
// Mutually exclusive observation exclusions, NOT page death/fault provenance.
public enum EpochExclusion { Known, OriginalMappingUnavailable, CurrentMappingUnavailable, AllocationChanged, RetainedAfterTrim }
public static class EpochEligibility
{
    public static EpochExclusion Classify(ulong original,ulong current,bool retained) =>
        original==0?EpochExclusion.OriginalMappingUnavailable:
        current==0?EpochExclusion.CurrentMappingUnavailable:
        current!=original?EpochExclusion.AllocationChanged:
        retained?EpochExclusion.RetainedAfterTrim:EpochExclusion.Known;
}