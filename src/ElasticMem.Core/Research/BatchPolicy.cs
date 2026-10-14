namespace ElasticMem.Core.Research;
public record ResearchCandidate(int Pid,string Family,long StartTicks,long WorkingSet,bool Renderer,bool NativeX64,bool SameSession);
public static class BatchPolicy
{
    static readonly HashSet<string> Allowed=new(StringComparer.OrdinalIgnoreCase){"chrome","msedge","steamwebhelper"};
    public static ResearchCandidate[] Select(IEnumerable<ResearchCandidate> candidates,int self,string? requiredFamily=null,int minimum=2)
    {
        return candidates.Where(c=>c.Pid>4&&c.Pid!=self&&c.Renderer&&c.NativeX64&&c.SameSession&&Allowed.Contains(c.Family)&&(requiredFamily==null||c.Family.Equals(requiredFamily,StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c=>c.Family,StringComparer.OrdinalIgnoreCase).Where(g=>g.Select(c=>c.Pid).Distinct().Count()>=minimum)
            .OrderByDescending(g=>g.Sum(c=>Math.Max(0,c.WorkingSet))).ThenBy(g=>g.Key,StringComparer.OrdinalIgnoreCase)
            .Select(g=>g.GroupBy(c=>c.Pid).Select(x=>x.First()).OrderByDescending(c=>c.WorkingSet).ThenBy(c=>c.Pid).Take(4).ToArray()).FirstOrDefault()??Array.Empty<ResearchCandidate>();
    }
    public static string ValidateBrowser(string preference)
    {
        if(preference is not("auto-browser" or "msedge" or "chrome" or "steamwebhelper"))throw new ArgumentException("Browser must be auto-browser,msedge,chrome or explicit steamwebhelper");
        return preference;
    }
    public static ResearchCandidate[] SelectBrowser(IEnumerable<ResearchCandidate> candidates,int self,string preference="auto-browser",int minimum=2)
    {
        ValidateBrowser(preference);var rows=candidates.ToArray();
        if(preference!="auto-browser")return Select(rows,self,preference,minimum);
        // Do not let a single Edge renderer hide a usable Chrome pair. Never silently choose Steam.
        var edge=Select(rows,self,"msedge",minimum);return edge.Length>=minimum?edge:Select(rows,self,"chrome",minimum);
    }
    public static bool Failure(string status)=>status=="completed_with_failures"||status.Contains("failed",StringComparison.OrdinalIgnoreCase)||status.StartsWith("invalid",StringComparison.Ordinal)||status.StartsWith("interrupted",StringComparison.Ordinal)||status.StartsWith("cancelled",StringComparison.Ordinal);
    public static string Conclusion(IEnumerable<string> statuses)
    {
        var s=statuses.ToArray();return s.Any(x=>x.StartsWith("cancelled",StringComparison.Ordinal))?"cancelled_partial_not_result":s.Any(Failure)?"completed_with_failures":s.Any(x=>x.StartsWith("skipped",StringComparison.Ordinal)||x.StartsWith("unsupported",StringComparison.Ordinal))?"completed_with_skips":"completed_checks_not_effectiveness_claim";
    }
}
