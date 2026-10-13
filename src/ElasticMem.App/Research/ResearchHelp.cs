using System.Reflection;
namespace ElasticMem.App.Research;
internal static class ResearchHelp
{
    public static string Text { get { using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ElasticMem.FullInstructions")??throw new InvalidOperationException("Embedded instructions missing");using var reader=new StreamReader(stream);return reader.ReadToEnd(); } }
    public static void Show(Form owner)
    {
        using var f=new Form{Text="ElasticMem — полная инструкция",Width=1000,Height=750,StartPosition=FormStartPosition.CenterParent};
        f.Controls.Add(new TextBox{Text=Text,ReadOnly=true,Multiline=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Segoe UI",10)});Theme.Apply(f,Theme.Dark);f.ShowDialog(owner);
    }
}
