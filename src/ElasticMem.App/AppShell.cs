namespace ElasticMem.App;
internal sealed class AppShell:UserControl
{
    readonly TabControl _tabs;readonly List<Button> _buttons=new();readonly Label _heading=new(){AutoSize=true,Font=new Font("Segoe UI",18,FontStyle.Bold),Padding=new Padding(20,16,0,0)};
    public AppShell(TabControl tabs,CheckBox theme)
    {
        Dock=DockStyle.Fill;_tabs=tabs;
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=Padding.Empty,Margin=Padding.Empty};grid.ColumnStyles.Add(new(SizeType.Absolute,192));grid.ColumnStyles.Add(new(SizeType.Percent,100));
        var rail=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(12,20,12,12),AccessibleDescription="surface"};rail.RowStyles.Add(new(SizeType.Absolute,76));rail.RowStyles.Add(new(SizeType.Percent,100));rail.RowStyles.Add(new(SizeType.Absolute,72));
        var brand=new Label{Text="ElasticMem\nWindows · Research",Dock=DockStyle.Fill,Font=new Font("Segoe UI",14,FontStyle.Bold),AutoSize=false};rail.Controls.Add(brand,0,0);
        var nav=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        foreach(TabPage page in tabs.TabPages){var p=page;var button=new Button{Text=p.Text,Width=160,Height=42,FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(8,0,0,0),Margin=new Padding(0,2,0,2),AccessibleName=p.Text};button.Click+=(_,_)=>tabs.SelectedTab=p;_buttons.Add(button);nav.Controls.Add(button);}rail.Controls.Add(nav,0,1);
        theme.Text="Тёмная тема";theme.AutoSize=true;rail.Controls.Add(theme,0,2);
        tabs.Appearance=TabAppearance.FlatButtons;tabs.ItemSize=new Size(0,1);tabs.SizeMode=TabSizeMode.Fixed;tabs.Margin=Padding.Empty;
        var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=Padding.Empty,Margin=Padding.Empty};body.RowStyles.Add(new(SizeType.Absolute,66));body.RowStyles.Add(new(SizeType.Percent,100));body.RowStyles.Add(new(SizeType.Absolute,32));body.Controls.Add(_heading,0,0);tabs.Dock=DockStyle.Fill;body.Controls.Add(tabs,0,1);
        body.Controls.Add(new Label{Text="  Локальные проверки · отчёты не доказывают универсальную экономию RAM",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AccessibleDescription="hint"},0,2);
        grid.Controls.Add(rail,0,0);grid.Controls.Add(body,1,0);Controls.Add(grid);tabs.SelectedIndexChanged+=(_,_)=>SelectPage();SelectPage();
    }
    public void RefreshPalette()=>SelectPage();
    void SelectPage(){_heading.Text=_tabs.SelectedTab?.Text??"ElasticMem";for(int i=0;i<_buttons.Count;i++){_buttons[i].AccessibleDescription=i==_tabs.SelectedIndex?"accent":"nav";Theme.Paint(_buttons[i]);}}
}
