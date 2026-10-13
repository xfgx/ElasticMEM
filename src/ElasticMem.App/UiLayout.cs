namespace ElasticMem.App;

/// <summary>
/// Вертикальная раскладка без перекрытий: каждая строка получает ровно ту высоту, которая ей нужна
/// при текущей ширине (с учётом переноса кнопок и текста), один элемент растягивается на остаток.
/// Заменяет Dock=Top + AutoSize, который в WinForms не пересчитывает высоту при переносе и при 125–200% DPI.
/// </summary>
public sealed class VStack : Panel
{
    private readonly List<Control> _items = new();
    private Control? _fill;

    public VStack() { Dock = DockStyle.Fill; Padding = new Padding(12); AutoScroll = true; DoubleBuffered = true; }

    public VStack Add(Control c, bool fill = false)
    {
        c.Dock = DockStyle.None;
        c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        if (c is FlowLayoutPanel f) { f.AutoSize = false; f.WrapContents = true; }
        if (c is Label l) l.AutoSize = false;
        _items.Add(c);
        if (fill) _fill = c;
        Controls.Add(c);
        return this;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = Math.Max(50, ClientSize.Width - Padding.Horizontal);
        var hs = new int[_items.Count];
        int used = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _items[i];
            if (!c.Visible || c == _fill) continue;
            if(c is FlowLayoutPanel flow)foreach(Control child in flow.Controls){int available=Math.Max(36,w-c.Margin.Horizontal-flow.Padding.Horizontal-child.Margin.Horizontal);if(!child.AutoSize && child.Width>available)child.Width=available;child.MaximumSize=new Size(available,0);}
            hs[i] = Measure(c, w);
            used += hs[i] + c.Margin.Vertical;
        }
        int minFill = _fill != null && _fill.Visible ? 180 : 0;
        AutoScrollMinSize = new Size(0, used + Padding.Vertical + minFill);
        int y = Padding.Top + AutoScrollPosition.Y;
        for (int i = 0; i < _items.Count; i++)
        {
            var c = _items[i];
            if (!c.Visible) continue;
            int h = c == _fill ? Math.Max(180, ClientSize.Height - Padding.Vertical - used - c.Margin.Vertical) : hs[i];
            c.SetBounds(Padding.Left + c.Margin.Left, y + c.Margin.Top, w - c.Margin.Horizontal, h);
            y += h + c.Margin.Vertical;
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PerformLayout(); }

    private static int Measure(Control c, int width)
    {
        int w = width - c.Margin.Horizontal;
        switch (c)
        {
            case Label l:
                var sz = TextRenderer.MeasureText(string.IsNullOrEmpty(l.Text) ? " " : l.Text, l.Font,
                    new Size(Math.Max(10, w - l.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                return sz.Height + l.Padding.Vertical + 2;
            case FlowLayoutPanel f:
                return FlowHeight(f, w);
            default:
                return c.Height;
        }
    }

    /// <summary>Та же логика переноса, что у FlowLayoutPanel (слева направо, с переносом).</summary>
    public static int FlowHeight(FlowLayoutPanel f, int width)
    {
        int left = f.Padding.Left, right = width - f.Padding.Right;
        int x = left, y = f.Padding.Top, rowH = 0;
        foreach (Control ch in f.Controls)
        {
            if (!ch.Visible) continue;
            var s = ch.AutoSize ? ch.GetPreferredSize(Size.Empty) : ch.Size;
            int cw = s.Width + ch.Margin.Horizontal, chh = s.Height + ch.Margin.Vertical;
            if (x + cw > right && x > left) { y += rowH; x = left; rowH = 0; }
            x += cw; rowH = Math.Max(rowH, chh);
        }
        return y + rowH + f.Padding.Bottom + 2;
    }
}

internal static class Ui
{
    public static FlowLayoutPanel Row(params Control[] cs)
    {
        var p = new FlowLayoutPanel { WrapContents = true, Padding = new Padding(0, 4, 0, 4) };
        foreach (var c in cs) { c.Margin = new Padding(0, 6, 12, 6); p.Controls.Add(c); }
        // при изменении текста статуса и т. п. — пересчитать высоту строки
        foreach (var c in cs) c.TextChanged += (_, _) => p.Parent?.PerformLayout();
        return p;
    }

    public static Label L(string t) => new() { Text = t, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };

    public static Label Hint(string t)
    {
        var l = new Label { Text = t, ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 12), AccessibleDescription = "hint" };
        l.TextChanged += (_, _) => l.Parent?.PerformLayout();
        return l;
    }
}
