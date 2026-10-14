using System.Text.Json;
namespace ElasticMem.Core.Research;
/// <summary>Known native allocation lifetime, not inferred from a missing mapping.</summary>
public static class LifecycleWorkload
{
    public const string Name="controlled_decommit_recommit_v1";
    public static (bool[][] Alive,bool[][] Touch,byte[][] Generation) Plan()
    {
        var alive=new bool[256][];var touch=new bool[256][];var generation=new byte[256][];var counter=new byte[128];
        for(int t=0;t<256;t++)
        {
            alive[t]=new bool[128];touch[t]=new bool[128];generation[t]=new byte[128];
            for(int p=0;p<128;p++)
            {
                bool live=(t+(p/16)*3)%32>=4;
                if(live&&(t==0||!alive[t-1][p]))counter[p]++;
                alive[t][p]=live;touch[t][p]=live&&(p+t)%11==0;generation[t][p]=counter[p];
            }
        }
        return(alive,touch,generation);
    }
    public static ulong Word(byte[] page,byte generation)=>BitConverter.ToUInt64(page,0)^((ulong)generation<<48);
    public static object Analyze(JsonElement row)
    {
        if(row.GetProperty("name").GetString()!=Name||row.GetProperty("pages").GetInt32()!=128||row.GetProperty("epochs").GetInt32()!=256)throw new InvalidDataException("Lifecycle shape/name");
        var plan=Plan();var alive=ControlledWorkload.Unpack(row.GetProperty("alive").GetString()!);var touch=ControlledWorkload.Unpack(row.GetProperty("touch").GetString()!);
        var before=ControlledWorkload.Unpack(row.GetProperty("before_valid").GetString()!);var after=ControlledWorkload.Unpack(row.GetProperty("after_valid").GetString()!);
        string generations=row.GetProperty("generations").GetString()!;
        if(generations.Length!=((128*256+2)/3)*4)throw new InvalidDataException("Lifecycle generation encoded length");
        byte[] gens=Convert.FromBase64String(generations);if(gens.Length!=128*256)throw new InvalidDataException("Lifecycle generation length");
        long reserved=0,reservedBefore=0,reservedAfter=0,active=0,dead=0,tp=0,fp=0,fn=0,tn=0,retained=0,deadBefore=0,deadAfter=0,commits=0,decommits=0;ulong checksum=0;
        var payload=ControlledWorkload.Contents();var epochs=new List<object>();
        for(int t=0;t<256;t++)
        {
            int epReserved=0,epLive=0,epTouch=0,epRetained=0,epCommits=0,epDecommits=0;
            for(int p=0;p<128;p++)
            {
                if(alive[t][p]!=plan.Alive[t][p]||touch[t][p]!=plan.Touch[t][p]||gens[t*128+p]!=plan.Generation[t][p])throw new InvalidDataException("Lifecycle schedule/generation mismatch");
                if(alive[t][p]&&(t==0||!alive[t-1][p])){commits++;epCommits++;}
                if(!alive[t][p]&&t>0&&alive[t-1][p]){decommits++;epDecommits++;}
                if(!alive[t][p]){if(plan.Generation[t][p]==0){reserved++;epReserved++;if(before[t][p])reservedBefore++;if(after[t][p])reservedAfter++;}else{dead++;if(before[t][p])deadBefore++;if(after[t][p])deadAfter++;}continue;}
                active++;epLive++;
                if(touch[t][p]){epTouch++;checksum=unchecked(checksum+Word(payload[p],plan.Generation[t][p]));}
                if(before[t][p]){retained++;epRetained++;continue;}
                if(touch[t][p]){if(after[t][p])tp++;else fn++;}
                else {if(after[t][p])fp++;else tn++;}
            }
            epochs.Add(new{epoch=t,active_pages=epLive,dead_pages=128-epLive-epReserved,reserved_never_committed_pages=epReserved,scheduled_reads=epTouch,retained_unknown=epRetained,committed_pages=epCommits,decommitted_pages=epDecommits});
        }
        if(row.GetProperty("checksum").GetUInt64()!=checksum||row.GetProperty("expected_checksum").GetUInt64()!=checksum||row.GetProperty("committed_pages").GetInt64()!=commits||row.GetProperty("decommitted_pages").GetInt64()!=decommits)throw new InvalidDataException("Lifecycle checksum/transition count mismatch");
        return new{name=Name,pages=128,epochs=256,active_page_epoch_cells=active,dead_page_epoch_cells=dead,reserved_never_committed_page_epoch_cells=reserved,known_active_cells=tp+fp+fn+tn,
            retained_active_unknown_cells=retained,query_failure_unknown_cells=0,known_true_positive=tp,known_false_negative=fn,known_false_positive=fp,known_true_negative=tn,
            known_touch_recall=tp+fn==0?(double?)null:(double)tp/(tp+fn),known_no_touch_false_positive_rate=fp+tn==0?(double?)null:(double)fp/(fp+tn),
            dead_reported_valid_before=deadBefore,dead_reported_valid_after=deadAfter,reserved_reported_valid_before=reservedBefore,reserved_reported_valid_after=reservedAfter,committed_pages=commits,decommitted_pages=decommits,checksum_verified=true,
            per_epoch=epochs,synthetic_trace=new{alive=row.GetProperty("alive").GetString(),touch=row.GetProperty("touch").GetString(),before_valid=row.GetProperty("before_valid").GetString(),after_valid=row.GetProperty("after_valid").GetString(),generations,layout="logical128pages x256epochs; bitsLSBfirst; generationbytesrowmajor; NOT native addresses"},
            causal_g5_status="skipped_lifecycle_not_supported_by_fixed_identity_causal_model",
            limitations=new[]{"Inactive cells with generation0 are reserved/never-committed,separate from post-MEM_DECOMMIT dead cells with generation>0; neither inferred from QWS failure/missing mappings","Recommit uses SAME reserved virtual address but a NEW logical generation; full page initialized before observation; only scheduled volatile reads inside observation interval","FN/FP conditional on active non-retained cells; dead and retained unknown are separate populations; QueryWorkingSet failure aborts whole worker,not converted to zero","No causal G5 gate on this trace: current grouping model does not implement per-generation page death; unchanged real-browser gate","Small single-thread own arena,deterministic decommit/recommit; not heap allocator,wholeprocessGC,real renderer,RAM saving or paging provenance"}};
    }
}
