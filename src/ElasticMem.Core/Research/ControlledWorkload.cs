using System.Text.Json;
namespace ElasticMem.Core.Research;
/// <summary>Finite synthetic arena only. Never a complete application/OS access trace.</summary>
public static class ControlledWorkload
{
    public const int Pages=128, Epochs=256, Seed=20019;
    public static readonly string[] Cases={"interleaved_correlated","adjacent_correlated","independent_fixed_density"};
    public static bool[][] Activity(string name)
    {
        if(!Cases.Contains(name))throw new ArgumentException("Unknown controlled case");
        var random=new Random(Seed);var rows=new bool[Epochs][];
        for(int t=0;t<Epochs;t++)
        {
            rows[t]=new bool[Pages];
            if(name=="independent_fixed_density")
            {
                // Eight unique pages per epoch; seeded once, no test-based selection.
                var order=Enumerable.Range(0,Pages).ToArray();
                for(int i=0;i<8;i++){int j=random.Next(i,Pages);(order[i],order[j])=(order[j],order[i]);rows[t][order[i]]=true;}
            }
            else if(t%17<16)
                for(int p=0;p<Pages;p++)rows[t][p]=(name=="interleaved_correlated"?p%16:p/8)==t%17;
        }
        return rows;
    }
    public static byte[][] Contents()
    {
        var result=new byte[Pages][];
        for(int p=0;p<Pages;p++)
        {
            var data=new byte[4096];new Random(Seed+p).NextBytes(data);
            // Fixed seeded synthetic payload, NOT renderer memory. No ratio generalization.
            for(int i=0;i<4096;i+=64){data[i]=(byte)p;data[i+1]=(byte)(p%16);}
            result[p]=data;
        }
        return result;
    }
    public static string Pack(bool[][] rows)
    {
        Validate(rows);var b=new byte[Pages*Epochs/8];
        for(int t=0;t<Epochs;t++)for(int p=0;p<Pages;p++)if(rows[t][p])b[(t*Pages+p)/8]|=(byte)(1<<((t*Pages+p)%8));
        return Convert.ToBase64String(b);
    }
    public static bool[][] Unpack(string packed)
    {
        // Enforce exact encoded bound before allocating decoded payload.
        if(packed.Length!=((Pages*Epochs/8+2)/3)*4)throw new InvalidDataException("Controlled trace encoded length");
        byte[] b=Convert.FromBase64String(packed);if(b.Length!=Pages*Epochs/8)throw new InvalidDataException("Controlled trace length");
        return Enumerable.Range(0,Epochs).Select(t=>Enumerable.Range(0,Pages).Select(p=>(b[(t*Pages+p)/8]&(1<<((t*Pages+p)%8)))!=0).ToArray()).ToArray();
    }
    public static void Validate(bool[][] rows)
    {
        if(rows.Length!=Epochs||rows.Any(r=>r.Length!=Pages))throw new InvalidDataException("Controlled trace shape");
    }
    public static (bool[][] Truth,bool[][] Observed,bool[][] Known) DecodeCase(JsonElement row,string expectedName)
    {
        if(!Cases.Contains(expectedName)||row.GetProperty("name").GetString()!=expectedName||
           row.GetProperty("pages").GetInt32()!=Pages||row.GetProperty("epochs").GetInt32()!=Epochs)throw new InvalidDataException("Controlled case name/dimensions");
        var truth=Unpack(row.GetProperty("truth").GetString()!);var observed=Unpack(row.GetProperty("observed").GetString()!);var known=Unpack(row.GetProperty("known").GetString()!);
        if(!truth.SelectMany(x=>x).SequenceEqual(Activity(expectedName).SelectMany(x=>x)))throw new InvalidDataException("Controlled truth differs from fixed schedule");
        var payload=Contents();ulong checksum=0;
        for(int t=0;t<Epochs;t++)for(int p=0;p<Pages;p++)
        {if(truth[t][p])checksum=unchecked(checksum+BitConverter.ToUInt64(payload[p],0));if(!known[t][p]&&observed[t][p])throw new InvalidDataException("Unknown proxy cell cannot be an observed return");}
        if(row.GetProperty("checksum").GetUInt64()!=checksum||row.GetProperty("expected_checksum").GetUInt64()!=checksum)throw new InvalidDataException("Controlled independently computed checksum");
        var timing=row.GetProperty("timing").EnumerateArray().ToArray();
        if(timing.Length!=Epochs||timing.Where((x,t)=>x.GetProperty("epoch").GetInt32()!=t||x.GetProperty("scheduled").GetInt32()!=truth[t].Count(v=>v)||x.GetProperty("retained").GetInt32()!=known[t].Count(v=>!v)||!double.IsFinite(x.GetProperty("touch_us").GetDouble())||!double.IsFinite(x.GetProperty("probe_and_touch_us").GetDouble())||x.GetProperty("touch_us").GetDouble()<0||x.GetProperty("probe_and_touch_us").GetDouble()<x.GetProperty("touch_us").GetDouble()).Any())throw new InvalidDataException("Controlled timing/coverage shape");
        return(truth,observed,known);
    }
    public static object Compare(bool[][] truth,bool[][] observed,bool[][] known)
    {
        Validate(truth);Validate(observed);Validate(known);
        long tp=0,fp=0,fn=0,tn=0,unknown=0;int unknownTest=0;
        var epochs=new List<object>();
        for(int t=0;t<Epochs;t++)
        {
            int kt=0,kf=0,missed=0,ut=0,uf=0;
            for(int p=0;p<Pages;p++)
            {
                if(!known[t][p]){unknown++;if(truth[t][p])ut++;else uf++;continue;}
                if(truth[t][p]){if(observed[t][p]){tp++;kt++;}else{fn++;missed++;}}
                else if(observed[t][p]){fp++;kf++;}else tn++;
            }
            if(t>=Epochs/2&&ut+uf>0)unknownTest++;
            epochs.Add(new{epoch=t,scheduled_pages=truth[t].Count(x=>x),true_positive=kt,false_positive=kf,false_negative=missed,unknown_scheduled=ut,unknown_unscheduled=uf});
        }
        return new{known_true_positive=tp,known_false_positive=fp,known_false_negative=fn,known_true_negative=tn,
            unknown_page_epoch_cells=unknown,known_fraction=(double)(tp+fp+fn+tn)/(Pages*Epochs),
            known_touch_recall=tp+fn==0?(double?)null:(double)tp/(tp+fn),
            known_no_touch_false_positive_rate=fp+tn==0?(double?)null:(double)fp/(fp+tn),
            unknown_test_epochs=unknownTest,per_epoch=epochs,
            limitations="Recall/false-positive rate conditional on known cells; retained-after-trim cells excluded,NOT assumed unaccessed; arena only"};
    }
}