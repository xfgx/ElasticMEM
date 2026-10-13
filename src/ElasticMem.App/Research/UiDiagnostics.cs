using System.Drawing.Imaging;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class UiDiagnostics
{
    public static async Task<string> Capture(Form form,string? root=null,bool screenshots=true,CancellationToken ct=default)
    {
        using var run=new ResearchRun(root??ResearchJobs.Root,"ui-audit",Updater.Current.ToString(),ResearchJobs.Commit,new{capture="all tabs, dark/light at CURRENT DPI; no desktop capture",screenshots});
        bool original=Theme.Dark;var tabs=Find<TabControl>(form);int selected=tabs?.SelectedIndex??0;var results=new List<object>();
        try
        {
            foreach(bool dark in new[]{true,false})
            {
                Theme.Apply(form,dark);
                for(int i=0;i<(tabs?.TabCount??1);i++)
                {
                    ct.ThrowIfCancellationRequested();if(tabs!=null)tabs.SelectedIndex=i;form.PerformLayout();await Task.Delay(100,ct);
                    var warnings=new List<string>();Audit(form,warnings);
                    if(screenshots){using var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));bitmap.Save(run.FilePath($"screenshots/{(dark?"dark":"light")}-{i}.png"),ImageFormat.Png);}
                    results.Add(new{theme=dark?"dark":"light",tab=tabs?.SelectedTab?.Text??"form",dpi=form.DeviceDpi,width=form.ClientSize.Width,height=form.ClientSize.Height,warnings});
                }
            }
            run.WriteJson("layout-audit.json",new{status=screenshots?"captured_not_visual_signoff":"bounds_only_not_visual_signoff",states=results,limitations=new[]{"Mechanical bounds audit cannot establish visual quality; inspect PNGs","Current DPI only; repeat on 100/125/150/200% scaling","DrawToBitmap may omit some native rendering; provide manual screenshot if needed"}});
            run.Seal("ui_capture_requires_human_review","Inspect layout-audit. Screenshots optional; no automatic claim of visual QA pass.");
        }
        catch(OperationCanceledException ex){run.Seal("cancelled_partial_not_result","UI audit cancelled",ex);throw;}
        catch(Exception ex){run.Seal("ui_capture_failed",ex.Message,ex);throw;}
        finally{if(tabs!=null)tabs.SelectedIndex=selected;Theme.Apply(form,original);}
        return run.DirectoryPath;
    }
    static T? Find<T>(Control c) where T:Control{if(c is T t)return t;foreach(Control ch in c.Controls){var found=Find<T>(ch);if(found!=null)return found;}return null;}
    static void Audit(Control parent,List<string> warnings)
    {
        foreach(Control c in parent.Controls)
        {
            if(!c.Visible)continue;var r=c.Bounds;
            if(!parent.AutoScrollEnabled()&&(r.Left<0||r.Top<0||r.Right>parent.ClientSize.Width+2||r.Bottom>parent.ClientSize.Height+2))warnings.Add($"Outside {parent.GetType().Name}: {c.GetType().Name} {c.Text[..Math.Min(60,c.Text.Length)]} bounds={r}");
            if(c is Button&&c.Height<30)warnings.Add("Small button: "+c.Text);
            Audit(c,warnings);
        }
        var children=parent.Controls.Cast<Control>().Where(c=>c.Visible&&c is not TabPage).ToArray();
        if(parent is FlowLayoutPanel or VStack)for(int i=0;i<children.Length;i++)for(int j=i+1;j<children.Length;j++)if(Rectangle.Intersect(children[i].Bounds,children[j].Bounds) is var intersection&&intersection.Width>2&&intersection.Height>2)warnings.Add("Overlapping controls: "+children[i].Text+" / "+children[j].Text);
    }
    static bool AutoScrollEnabled(this Control c)=>c is ScrollableControl s&&s.AutoScroll;
}
