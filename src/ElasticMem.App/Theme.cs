using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace ElasticMem.App;
public static class Theme
{
    public static bool Dark{get;private set;}
    public static readonly Color DBg=Color.FromArgb(21,26,35),DPanel=Color.FromArgb(28,35,48),DCtl=Color.FromArgb(37,46,63),DBorder=Color.FromArgb(66,82,106),DFg=Color.FromArgb(237,242,248),DHint=Color.FromArgb(172,185,204),DAccent=Color.FromArgb(52,111,190),LAccent=Color.FromArgb(221,237,255);
    public static bool SystemPrefersDark(){try{using var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");return k?.GetValue("AppsUseLightTheme")is int v&&v==0;}catch{return false;}}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int val,int size);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]static extern int SetWindowTheme(IntPtr h,string? app,string? ids);
    public static void Apply(Form f,bool dark){Dark=dark;if(f.IsHandleCreated)TitleBar(f);else if(f.Tag as string!="theme-hook"){f.HandleCreated+=(_,_)=>TitleBar(f);f.Tag="theme-hook";}f.SuspendLayout();Paint(f);f.ResumeLayout(true);f.Invalidate(true);}
    static void TitleBar(Form f){try{int v=Dark?1:0;if(DwmSetWindowAttribute(f.Handle,20,ref v,4)!=0)DwmSetWindowAttribute(f.Handle,19,ref v,4);}catch{}}
    public static void Paint(Control c)
    {
        var background=Dark?DBg:Color.FromArgb(246,248,252);var foreground=Dark?DFg:Color.FromArgb(30,41,59);var surface=Dark?DPanel:Color.White;var control=Dark?DCtl:Color.White;
        c.ForeColor=foreground;c.BackColor=c.AccessibleDescription=="surface"?surface:background;
        switch(c)
        {
            case TextBox t:t.BackColor=t.Multiline?surface:control;t.ForeColor=foreground;t.BorderStyle=BorderStyle.FixedSingle;break;
            case ListView lv:lv.BackColor=surface;lv.ForeColor=foreground;lv.BorderStyle=BorderStyle.FixedSingle;lv.HideSelection=false;if(!lv.OwnerDraw)HookHeader(lv);if(lv.IsHandleCreated)try{SetWindowTheme(lv.Handle,Dark?"DarkMode_Explorer":"Explorer",null);}catch{}break;
            case NumericUpDown n:n.BackColor=control;n.ForeColor=foreground;n.BorderStyle=BorderStyle.FixedSingle;break;
            case ComboBox cb:cb.BackColor=control;cb.ForeColor=foreground;cb.FlatStyle=FlatStyle.Flat;break;
            case Button b:b.FlatStyle=FlatStyle.Flat;b.UseVisualStyleBackColor=false;b.BackColor=c.AccessibleDescription=="accent"?(Dark?DAccent:Color.FromArgb(38,105,190)):control;b.ForeColor=c.AccessibleDescription=="accent"?Color.White:foreground;b.FlatAppearance.BorderColor=Dark?DBorder:Color.FromArgb(202,212,225);b.FlatAppearance.BorderSize=c.AccessibleDescription is "nav" or "accent"?0:1;b.FlatAppearance.MouseOverBackColor=Dark?Color.FromArgb(55,70,91):Color.FromArgb(229,239,252);b.MinimumSize=new Size(b.Width<=40?36:76,36);b.Padding=new Padding(Math.Max(6,b.Padding.Left),4,Math.Max(6,b.Padding.Right),4);break;
            case CheckBox check:check.FlatStyle=FlatStyle.Standard;check.UseVisualStyleBackColor=false;check.ForeColor=foreground;break;
            case RadioButton radio:radio.UseVisualStyleBackColor=false;radio.ForeColor=foreground;break;
            case Label label:label.BackColor=Color.Transparent;if(label.AccessibleDescription=="hint")label.ForeColor=Dark?DHint:Color.FromArgb(80,96,118);break;
            case TabPage page:page.UseVisualStyleBackColor=false;page.BackColor=background;break;
            case TableLayoutPanel panel:panel.BackColor=panel.AccessibleDescription=="surface"?surface:background;break;
        }
        if(c is NumericUpDown or ComboBox or ListView)return;
        foreach(Control child in c.Controls)Paint(child);
    }
    static void HookHeader(ListView lv)
    {
        lv.OwnerDraw=true;lv.DrawColumnHeader+=(_,e)=>{using var brush=new SolidBrush(Dark?DCtl:Color.FromArgb(232,238,247));e.Graphics.FillRectangle(brush,e.Bounds);using var pen=new Pen(Dark?DBorder:Color.FromArgb(202,212,225));e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);TextRenderer.DrawText(e.Graphics,e.Header?.Text,lv.Font,Rectangle.Inflate(e.Bounds,-8,0),Dark?DFg:Color.FromArgb(40,54,73),TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);};lv.DrawItem+=(_,e)=>e.DrawDefault=true;lv.DrawSubItem+=(_,e)=>e.DrawDefault=true;
    }
}
