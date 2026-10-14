namespace ElasticMem.Core.Backend.Vmm;
// Reference bits cover ONLY accesses reported to ObserveAccess. No claim of complete guest working set.
// WHP memory exits alone miss hits on resident mappings; no dirty bit is falsely treated as read activity.
public sealed class ClockPagePolicy
{
    readonly bool[] reference;int hand;
    public ClockPagePolicy(int pages){if(pages is <1 or >4096)throw new ArgumentOutOfRangeException(nameof(pages));reference=new bool[pages];}
    public void ObserveAccess(int page){if((uint)page>=(uint)reference.Length)throw new ArgumentOutOfRangeException(nameof(page));reference[page]=true;}
    public int? Choose(Func<int,bool> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        for(int n=0;n<reference.Length*2;n++){int p=hand;hand=(hand+1)%reference.Length;if(!eligible(p))continue;if(reference[p]){reference[p]=false;continue;}return p;}return null;
    }
}
