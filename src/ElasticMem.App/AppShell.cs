namespace ElasticMem.App;

/// <summary>
/// Боковое меню приложения: слева — крупные понятные кнопки-разделы и переключатель темы,
/// сверху — заголовок текущего раздела. Ничего «исследовательского» в главном окне нет.
/// </summary>
internal sealed class AppShell : UserControl
{
    readonly TableLayoutPanel _grid;
    readonly FlowLayoutPanel _nav;
    readonly TabControl _tabs;
    readonly List<Button> _buttons = new();
    readonly Label _heading = new() { AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), Padding = new Padding(20, 16, 0, 0) };

    public AppShell(TabControl tabs, CheckBox theme)
    {
        Dock = DockStyle.Fill; _tabs = tabs;
        _grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        _grid.ColumnStyles.Add(new(SizeType.Absolute, 210));
        _grid.ColumnStyles.Add(new(SizeType.Percent, 100));

        var rail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12, 20, 12, 12), AccessibleDescription = "surface" };
        rail.ColumnStyles.Add(new(SizeType.Percent, 100));
        rail.RowStyles.Add(new(SizeType.Absolute, 64));
        rail.RowStyles.Add(new(SizeType.Percent, 100));
        rail.RowStyles.Add(new(SizeType.Absolute, 40));
        var brand = new Label { Text = "ElasticMem\nСжатие памяти", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = false, AutoEllipsis = true };
        rail.Controls.Add(brand, 0, 0);

        _nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (TabPage page in tabs.TabPages)
        {
            var p = page;
            var button = new Button { Text = p.Text, Width = 176, Height = 44, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Margin = new Padding(0, 2, 0, 2), AccessibleName = p.Text, AutoEllipsis = true, Font = new Font("Segoe UI", 10.5f) };
            button.Click += (_, _) => tabs.SelectedTab = p;
            _buttons.Add(button); _nav.Controls.Add(button);
        }
        rail.Controls.Add(_nav, 0, 1);
        theme.Text = "🌙 Тёмная тема"; theme.AutoSize = true; rail.Controls.Add(theme, 0, 2);

        tabs.Appearance = TabAppearance.FlatButtons; tabs.ItemSize = new Size(0, 1); tabs.SizeMode = TabSizeMode.Fixed; tabs.Margin = Padding.Empty;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Padding.Empty, Margin = Padding.Empty };
        body.RowStyles.Add(new(SizeType.Absolute, 60));
        body.RowStyles.Add(new(SizeType.Percent, 100));
        body.Controls.Add(_heading, 0, 0);
        tabs.Dock = DockStyle.Fill; body.Controls.Add(tabs, 0, 1);

        _grid.Controls.Add(rail, 0, 0); _grid.Controls.Add(body, 1, 0); Controls.Add(_grid);
        tabs.SelectedIndexChanged += (_, _) => SelectPage(); SelectPage();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_grid == null || _nav == null) return;
        int scaled = (int)Math.Ceiling(210 * DeviceDpi / 96d);
        int longest = _buttons.Select(b => TextRenderer.MeasureText(b.Text, b.Font).Width + b.Padding.Horizontal + b.Margin.Horizontal + 32).DefaultIfEmpty(200).Max();
        int desired = Math.Min(Math.Max(scaled, longest + 24), Math.Max(176, ClientSize.Width - (int)Math.Ceiling(480 * DeviceDpi / 96d)));
        if (Math.Abs(_grid.ColumnStyles[0].Width - desired) > 1) _grid.ColumnStyles[0].Width = desired;
        int width = Math.Max(176, _nav.ClientSize.Width - _nav.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (var b in _buttons) if (b.Width != width) b.Width = width;
    }

    public void RefreshPalette() => SelectPage();

    void SelectPage()
    {
        _heading.Text = _tabs.SelectedTab?.Text ?? "ElasticMem";
        for (int i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].AccessibleDescription = i == _tabs.SelectedIndex ? "accent" : "nav";
            Theme.Paint(_buttons[i]);
        }
    }
}
