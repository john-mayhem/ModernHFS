using System.Drawing.Drawing2D;

namespace ModernHFS;

sealed class FlatButton : Control
{
    bool hover, down, primary;

    public FlatButton(string text, bool primary = false)
    {
        Text = text;
        this.primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    /// <summary>Color behind the rounded corners (the card or window the button sits on).</summary>
    public Color Backdrop { get; set; } = Theme.Bg;

    /// <summary>Segoe Fluent Icons glyph drawn before the text (or alone when Text is empty).</summary>
    public char Glyph { get => glyph; set { glyph = value; Invalidate(); } }
    char glyph = ModernHFS.Glyph.None;

    /// <summary>Fluent "subtle" button: no fill or border until hovered.</summary>
    public bool Subtle { get; set; }

    Font? iconFont;
    Font IconFont => iconFont ??= UiFont.Icon(Font.SizeInPoints * 0.95f);
    protected override void OnFontChanged(EventArgs e) { iconFont?.Dispose(); iconFont = null; base.OnFontChanged(e); }

    public bool Primary
    {
        get => primary;
        set { if (primary != value) { primary = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Backdrop);
        Color fill, text;
        Color? border = null;
        if (primary)
        {
            fill = !Enabled ? Theme.BorderStrong : (hover || down) ? Theme.AccentHover : Theme.Accent;
            text = Theme.OnAccent;
        }
        else if (Subtle)
        {
            fill = (hover || down) && Enabled ? Theme.Hover : Backdrop;
            text = Enabled ? Theme.Text : Theme.SubText;
        }
        else
        {
            fill = (hover || down) && Enabled ? Theme.Hover : Theme.Surface;
            border = Theme.Border;
            text = Enabled ? Theme.Text : Theme.SubText;
        }
        if (Focused && ShowFocusCues) border = Theme.Accent;
        Theme.FillRound(g, ClientRectangle, fill, border, this.S(6));

        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        bool hasIcon = glyph != ModernHFS.Glyph.None;
        var textSize = Text.Length > 0 ? TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags) : Size.Empty;
        var iconSize = hasIcon ? TextRenderer.MeasureText(g, glyph.ToString(), IconFont, Size.Empty, flags | TextFormatFlags.NoPadding) : Size.Empty;
        int gap = hasIcon && Text.Length > 0 ? this.S(6) : 0;
        int x = (Width - (iconSize.Width + gap + textSize.Width)) / 2;
        if (hasIcon)
        {
            TextRenderer.DrawText(g, glyph.ToString(), IconFont, new Rectangle(x, 0, iconSize.Width, Height + this.S(1)),
                primary ? text : Subtle || !Enabled ? text : Theme.AccentText, fill, flags | TextFormatFlags.NoPadding);
            x += iconSize.Width + gap;
        }
        if (Text.Length > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), text, fill, flags);
    }

    /// <summary>Width that fits icon + text with comfortable padding.</summary>
    public int PreferredWidth(int minimum96 = 0)
    {
        using var g = CreateGraphics();
        int w = Text.Length > 0 ? TextRenderer.MeasureText(g, Text, Font).Width : 0;
        if (glyph != ModernHFS.Glyph.None) w += TextRenderer.MeasureText(g, glyph.ToString(), IconFont).Width + this.S(2);
        return Math.Max(this.S(minimum96), w + this.S(Text.Length > 0 ? 22 : 12));
    }
}

/// <summary>Rounded single-line text field.</summary>
sealed class TextField : Control
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    public event Action? Committed;
    public bool DigitsOnly { get; set; }
    public Color Backdrop { get; set; } = Theme.Bg;

    public TextField()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        Box.BackColor = Theme.Surface;
        Box.ForeColor = Theme.Text;
        Box.KeyPress += (_, e) =>
        {
            if (DigitsOnly && !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true;
        };
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Committed?.Invoke(); }
        };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
    }

    public string Placeholder { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

    public bool TryGetPort(out int port) => int.TryParse(Box.Text, out port) && port is > 0 and < 65536;

    protected override void OnFontChanged(EventArgs e) { Box.Font = Font; PerformLayout(); base.OnFontChanged(e); }
    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Box.PreferredHeight;
        Box.Bounds = new Rectangle(this.S(9), (Height - h) / 2, Math.Max(10, Width - this.S(18)), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Backdrop);
        Theme.FillRound(g, ClientRectangle, Theme.Surface, Box.Focused ? Theme.Accent : Theme.BorderStrong, this.S(6));
        if (Box.Focused)
            using (var p = new Pen(Theme.Accent, this.S(2)))   // Fluent focus: accent underline
                g.DrawLine(p, this.S(5), Height - this.S(1), Width - this.S(5), Height - this.S(1));
    }
}

/// <summary>Big dashed drop target shown while nothing is shared.</summary>
sealed class DropPanel : Control
{
    bool highlight;
    public Font? TitleFont { get; set; }

    public DropPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    public bool Highlight
    {
        get => highlight;
        set { if (highlight != value) { highlight = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);
        int inset = this.S(8);
        var r = new RectangleF(inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
        using (var path = Theme.Round(r, this.S(10)))
        {
            using (var b = new SolidBrush(highlight ? Theme.AccentSoft : Theme.Surface)) g.FillPath(b, path);
            using var pen = new Pen(highlight ? Theme.Accent : Theme.BorderStrong, this.S(1.5f)) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
        }

        int cx = Width / 2, cy = Height / 2;
        int d = this.S(52);
        var circle = new Rectangle(cx - d / 2, cy - d - this.S(10), d, d);
        using (var b = new SolidBrush(highlight ? Theme.Accent : Theme.Hover)) g.FillEllipse(b, circle);
        using (var pen = new Pen(highlight ? Color.White : Theme.SubText, this.S(2.5f))
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float ax = circle.X + d / 2f, top = circle.Y + d * 0.26f, bottom = circle.Y + d * 0.62f;
            g.DrawLine(pen, ax, top, ax, bottom);
            g.DrawLines(pen, [new PointF(ax - d * 0.15f, top + d * 0.2f), new PointF(ax, bottom + 1), new PointF(ax + d * 0.15f, top + d * 0.2f)]);
            g.DrawLine(pen, ax - d * 0.2f, circle.Y + d * 0.76f, ax + d * 0.2f, circle.Y + d * 0.76f);
        }

        var bg = highlight ? Theme.AccentSoft : Theme.Surface;
        var title = new Rectangle(0, cy + this.S(2), Width, this.S(28));
        TextRenderer.DrawText(g, highlight ? "Drop to share" : "Drop files or folders here", TitleFont ?? Font, title,
            highlight ? Theme.Accent : Theme.Text, bg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var sub = new Rectangle(this.S(24), title.Bottom + this.S(2), Width - this.S(48), this.S(44));
        TextRenderer.DrawText(g, "They're shared right away. Shared folders stay live: new files show up on their own.",
            Font, sub, Theme.SubText, bg, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}

/// <summary>Owner-drawn list of shared items (the form draws the rows).</summary>
sealed class ItemList : ListView
{
    public ItemList()
    {
        View = View.Details;
        FullRowSelect = true;
        HeaderStyle = ColumnHeaderStyle.None;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        MultiSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Columns.Add("", 100);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F) GridList.PaintBelowRows(this, null);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (Columns.Count > 0 && ClientSize.Width > 0) Columns[0].Width = ClientSize.Width;
    }
}

/// <summary>Themed multi-column list: connections, log, statistics, bans.</summary>
sealed class GridList : ListView
{
    int hover = -1;
    readonly int rowHeight;

    /// <summary>Text color for a cell; null = default (first column bright, the rest dimmed).</summary>
    public Func<ListViewItem, int, Color?>? CellColor;
    /// <summary>Custom painter for a cell; return true when it drew the cell.</summary>
    public Func<DrawListViewSubItemEventArgs, Rectangle, Color, bool>? CustomCell;
    public int FillColumn { get; set; } = -1;
    public string? EmptyText { get; set; }
    public Font? HeaderFont { get; set; }

    public GridList(int rowHeight96 = 30)
    {
        View = View.Details;
        FullRowSelect = true;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        HideSelection = false;
        DoubleBuffered = true;
        MultiSelect = true;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        rowHeight = rowHeight96;
        SmallImageList = new ImageList { ImageSize = new Size(1, this.S(rowHeight96)) };
        DrawColumnHeader += OnDrawHeader;
        DrawItem += (_, _) => { };
        DrawSubItem += OnDrawCell;
    }

    public void AddColumn(string text, int width96, HorizontalAlignment align = HorizontalAlignment.Left) =>
        Columns.Add(text, this.S(width96), align);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null);
        var header = Native.SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero) Native.SetWindowTheme(header, Theme.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
        FitColumns();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitColumns();
    }

    public void FitColumns()
    {
        if (Columns.Count == 0 || ClientSize.Width <= 0) return;
        int fill = FillColumn < 0 || FillColumn >= Columns.Count ? Columns.Count - 1 : FillColumn;
        int others = 0;
        for (int i = 0; i < Columns.Count; i++) if (i != fill) others += Columns[i].Width;
        int w = Math.Max(this.S(80), ClientSize.Width - others - this.S(2));   // exact fit still triggers a horizontal scrollbar
        if (Columns[fill].Width != w) Columns[fill].Width = w;
        // An empty or virtual list doesn't recompute its scroll range on column resize; poke it and drop the stale bar.
        if (!IsHandleCreated) return;
        if (VirtualMode) Native.SendMessage(Handle, 0x1000 + 47 /* LVM_SETITEMCOUNT */, VirtualListSize, 2);
        if (others + w <= ClientSize.Width) Native.ShowScrollBar(Handle, 0 /* SB_HORZ */, false);
    }

    static TextFormatFlags Align(HorizontalAlignment a) => a switch
    {
        HorizontalAlignment.Right => TextFormatFlags.Right,
        HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
        _ => TextFormatFlags.Left,
    };

    void OnDrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.Bounds;
        if (e.ColumnIndex == Columns.Count - 1) r.Width = Math.Max(r.Width, ClientSize.Width - r.X + this.S(40));
        using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, r);
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        var tr = Rectangle.Inflate(e.Bounds, -this.S(10), 0);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", HeaderFont ?? Font, tr, Theme.SubText, Theme.HeaderBg,
            Align(e.Header?.TextAlign ?? HorizontalAlignment.Left) | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void OnDrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null) return;
        var g = e.Graphics;
        var r = e.Bounds;
        if (e.ColumnIndex == 0) r.Width = Columns[0].Width;
        if (e.ColumnIndex == Columns.Count - 1) r.Width = Math.Max(r.Width, ClientSize.Width - r.X);
        var bg = e.Item.Selected ? Theme.Selected : e.ItemIndex == hover ? Theme.Hover : Theme.Surface;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        if (e.Item.Selected && e.ColumnIndex == 0) SelectionBar(g, this, r);
        if (CustomCell != null && CustomCell(e, r, bg)) return;
        var color = CellColor?.Invoke(e.Item, e.ColumnIndex) ?? (e.ColumnIndex == 0 ? Theme.Text : Theme.SubText);
        var tr = Rectangle.Inflate(r, -this.S(10), 0);
        TextRenderer.DrawText(g, e.SubItem?.Text ?? "", Font, tr, color, bg,
            Align(Columns[e.ColumnIndex].TextAlign) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(GetItemAt(e.X, e.Y)?.Index ?? -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    void SetHover(int index)
    {
        if (index == hover) return;
        int old = hover;
        hover = index;
        int count = VirtualMode ? VirtualListSize : Items.Count;
        if (old >= 0 && old < count) Invalidate(GetItemRect(old));
        if (index >= 0 && index < count) Invalidate(GetItemRect(index));
    }

    /// <summary>Fluent list selection: a short accent pill on the row's left edge.</summary>
    internal static void SelectionBar(Graphics g, Control c, Rectangle row)
    {
        int h = Math.Min(c.S(16), row.Height - c.S(10));
        var bar = new Rectangle(row.X + c.S(2), row.Y + (row.Height - h) / 2, c.S(3), h);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(g, bar, Theme.Accent, null, bar.Width / 2f);
        g.SmoothingMode = mode;
    }

    public bool AtBottom()
    {
        int count = VirtualMode ? VirtualListSize : Items.Count;
        if (count == 0 || !IsHandleCreated) return true;
        try { return GetItemRect(count - 1).Bottom <= ClientSize.Height + this.S(rowHeight) * 2; }
        catch { return true; }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F) PaintBelowRows(this, EmptyText);
    }

    /// <summary>
    /// The themed ListView draws column dividers in the empty area under the rows; paint over that area
    /// (and show the "nothing here" text when the list is empty).
    /// </summary>
    internal static void PaintBelowRows(ListView lv, string? emptyText)
    {
        int count = lv.VirtualMode ? lv.VirtualListSize : lv.Items.Count;
        int top = 0;
        if (count > 0)
        {
            try { top = lv.GetItemRect(count - 1).Bottom; } catch { return; }
        }
        else if (lv.HeaderStyle != ColumnHeaderStyle.None && lv.Columns.Count > 0)
        {
            top = lv.Font.Height + lv.S(10);
        }
        if (top >= lv.ClientSize.Height) return;
        top = Math.Max(0, top);
        using var g = lv.CreateGraphics();
        using (var b = new SolidBrush(lv.BackColor)) g.FillRectangle(b, 0, top, lv.ClientSize.Width, lv.ClientSize.Height - top);
        if (count == 0 && !string.IsNullOrEmpty(emptyText))
            TextRenderer.DrawText(g, emptyText, lv.Font, new Rectangle(0, top, lv.ClientSize.Width, Math.Min(lv.S(80), lv.ClientSize.Height - top)),
                Theme.SubText, lv.BackColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Rounded panel with a header (title or tabs, action controls on the right) and one content control.</summary>
sealed class Card : Control
{
    readonly List<Control> actions = [];
    Control? content;
    Rectangle[] tabRects = [];
    int tab;
    bool highlight;
    string badge = "", note = "";

    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    public string Title { get; set; } = "";
    public string[] Tabs { get; set; } = [];
    public Font? TitleFont { get; set; }
    public int HeaderHeight96 { get; set; } = 44;
    public event Action<int>? TabChanged;

    int HeaderH => this.S(HeaderHeight96);

    public string Badge { get => badge; set { if (badge != value) { badge = value; Invalidate(new Rectangle(0, 0, Width, HeaderH)); } } }
    public string Note { get => note; set { if (note != value) { note = value; Invalidate(new Rectangle(0, 0, Width, HeaderH)); } } }
    public bool Highlight { get => highlight; set { if (highlight != value) { highlight = value; Invalidate(); } } }

    public int Tab
    {
        get => tab;
        set { if (tab != value) { tab = value; Invalidate(); TabChanged?.Invoke(value); } }
    }

    public void SetContent(Control c)
    {
        if (content == c) return;
        SuspendLayout();
        if (content != null) Controls.Remove(content);
        content = c;
        Controls.Add(c);
        ResumeLayout(true);
    }

    public void SetActions(params Control[] list)
    {
        SuspendLayout();
        foreach (var a in actions) Controls.Remove(a);
        actions.Clear();
        actions.AddRange(list);
        foreach (var a in actions) Controls.Add(a);
        ResumeLayout(true);
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = this.S(30);
        int x = Width - this.S(10);
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            var a = actions[i];
            a.Bounds = new Rectangle(x - a.Width, (HeaderH - h) / 2, a.Width, h);
            x -= a.Width + this.S(6);
        }
        int pad = this.S(5);
        if (content != null)
            content.Bounds = new Rectangle(pad, HeaderH, Math.Max(0, Width - 2 * pad), Math.Max(0, Height - HeaderH - pad - this.S(3)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Theme.FillRound(g, ClientRectangle, Theme.Surface, highlight ? Theme.Accent : Theme.Border, this.S(10), highlight ? this.S(2) : 1);
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, 1, HeaderH - 1, Width - 2, HeaderH - 1);

        var font = TitleFont ?? Font;
        const TextFormatFlags one = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        int x = this.S(14);
        if (Tabs.Length > 0)
        {
            // segmented control: a sunken track with the active segment raised
            tabRects = new Rectangle[Tabs.Length];
            int ph = this.S(30), pad = this.S(3);
            var widths = Tabs.Select(t => TextRenderer.MeasureText(t, font).Width + this.S(22)).ToArray();
            var track = new Rectangle(this.S(9), (HeaderH - ph) / 2, widths.Sum() + pad * 2, ph);
            Theme.FillRound(g, track, Theme.HeaderBg, Theme.Border, this.S(7));
            x = track.X + pad;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rectangle(x, track.Y + pad, widths[i], ph - pad * 2);
                tabRects[i] = r;
                bool active = i == tab;
                if (active) Theme.FillRound(g, r, Theme.Surface, Theme.BorderStrong, this.S(5));
                TextRenderer.DrawText(g, Tabs[i], font, r, active ? Theme.Text : Theme.SubText, active ? Theme.Surface : Theme.HeaderBg,
                    one | TextFormatFlags.HorizontalCenter);
                x = r.Right;
            }
            x = track.Right + this.S(6);
        }
        else
        {
            var size = TextRenderer.MeasureText(Title, font);
            TextRenderer.DrawText(g, Title, font, new Rectangle(x, 0, size.Width + 4, HeaderH), Theme.Text, Theme.Surface, one);
            x += size.Width + this.S(6);
            if (badge.Length > 0)
            {
                var bs = TextRenderer.MeasureText(badge, Font);
                var br = new Rectangle(x, (HeaderH - this.S(20)) / 2, bs.Width + this.S(10), this.S(20));
                Theme.FillRound(g, br, Theme.Hover, null, this.S(10));
                TextRenderer.DrawText(g, badge, Font, br, Theme.SubText, Theme.Hover, one | TextFormatFlags.HorizontalCenter);
                x = br.Right + this.S(8);
            }
        }
        if (note.Length > 0)
        {
            int right = actions.Count > 0 ? actions.Min(a => a.Left) - this.S(10) : Width - this.S(14);
            var nr = new Rectangle(x + this.S(10), 0, Math.Max(0, right - x - this.S(10)), HeaderH);
            TextRenderer.DrawText(g, note, Font, nr, Theme.SubText, Theme.Surface, one | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = tabRects.Any(r => r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        for (int i = 0; i < tabRects.Length; i++)
            if (tabRects[i].Contains(e.Location)) Tab = i;
    }
}

/// <summary>Scrolling out/in speed chart, one sample per second.</summary>
sealed class BandwidthGraph : Control
{
    const int N = 180;
    readonly double[] outs = new double[N], ins = new double[N];

    public BandwidthGraph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    public double Peak => outs.Max();

    public void Push(double outBps, double inBps)
    {
        Array.Copy(outs, 1, outs, 0, N - 1);
        Array.Copy(ins, 1, ins, 0, N - 1);
        outs[N - 1] = outBps;
        ins[N - 1] = inBps;
        Invalidate();
    }

    static double NiceCeiling(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * p >= v) return m * p;
        return 10 * p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(this.S(6), this.S(4), Width - this.S(12), Height - this.S(8));
        if (r.Width < 10 || r.Height < 10) return;

        double max = 16 * 1024;
        for (int i = 0; i < N; i++) max = Math.Max(max, Math.Max(outs[i], ins[i]));
        double unit = max * 1.1 >= 1000 * 1024 ? 1024 * 1024 : 1024;   // round in KB or MB: 20 KB/s, 50 MB/s...
        max = NiceCeiling(max * 1.1 / unit) * unit;

        using (var grid = new Pen(Theme.Border) { DashStyle = DashStyle.Dot })
            for (int k = 1; k <= 3; k++)
            {
                float y = r.Top + r.Height * k / 4f;
                g.DrawLine(grid, r.Left, y, r.Right, y);
            }

        PointF P(int i, double v) => new(r.Left + i * r.Width / (N - 1), r.Bottom - (float)(v / max * r.Height));
        var outPts = Enumerable.Range(0, N).Select(i => P(i, outs[i])).ToArray();
        var inPts = Enumerable.Range(0, N).Select(i => P(i, ins[i])).ToArray();

        using (var fill = new GraphicsPath())
        {
            fill.AddLines(outPts);
            fill.AddLine(outPts[^1], new PointF(r.Right, r.Bottom));
            fill.AddLine(new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom));
            fill.CloseFigure();
            using var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
                Color.FromArgb(Theme.Dark ? 110 : 80, Theme.Accent), Color.FromArgb(0, Theme.Accent), LinearGradientMode.Vertical);
            g.FillPath(b, fill);
        }
        using (var p = new Pen(Theme.Accent, this.S(1.6f)) { LineJoin = LineJoin.Round }) g.DrawLines(p, outPts);
        using (var p = new Pen(Theme.Good, this.S(1.3f)) { LineJoin = LineJoin.Round }) g.DrawLines(p, inPts);

        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, Fmt.Speed(max), Font, new Point((int)r.Left + this.S(2), (int)r.Top), Theme.SubText);
    }
}

/// <summary>Row of number tiles (statistics summary).</summary>
sealed class StatTiles : Control
{
    (string Label, string Value)[] tiles = [];
    public Font? ValueFont { get; set; }
    public Font? LabelFont { get; set; }

    public StatTiles()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    public (string Label, string Value)[] Tiles
    {
        get => tiles;
        set { tiles = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        if (tiles.Length == 0) return;
        int cols = Width >= this.S(560) ? tiles.Length : (tiles.Length + 1) / 2;
        int rows = (tiles.Length + cols - 1) / cols;
        int w = Width / cols, h = Height / rows;
        using var line = new Pen(Theme.Border);
        for (int i = 0; i < tiles.Length; i++)
        {
            int cx = (i % cols) * w, cy = (i / cols) * h;
            if (i % cols > 0) g.DrawLine(line, cx, cy + this.S(10), cx, cy + h - this.S(10));
            TextRenderer.DrawText(g, tiles[i].Label.ToUpperInvariant(), LabelFont ?? Font, new Point(cx + this.S(14), cy + this.S(10)), Theme.SubText);
            TextRenderer.DrawText(g, tiles[i].Value, ValueFont ?? Font, new Rectangle(cx + this.S(12), cy + this.S(28), w - this.S(16), this.S(26)),
                Theme.Text, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Statistics tab: tiles on top, most-downloaded files and visitors side by side.</summary>
sealed class StatsView : Control
{
    public readonly StatTiles Tiles = new();
    public readonly GridList Files = new(28) { FillColumn = 0, EmptyText = "No finished downloads yet" };
    public readonly GridList Visitors = new(28) { FillColumn = 1, EmptyText = "Nobody has visited yet" };

    public StatsView()
    {
        Files.AddColumn("Most downloaded", 200);
        Files.AddColumn("Times", 64, HorizontalAlignment.Right);
        Visitors.AddColumn("Visitor", 120);
        Visitors.AddColumn("Host", 150);
        Visitors.AddColumn("Visits", 60, HorizontalAlignment.Right);
        Visitors.AddColumn("Files", 50, HorizontalAlignment.Right);
        Visitors.AddColumn("Sent", 76, HorizontalAlignment.Right);
        Visitors.AddColumn("Last seen", 116);
        Controls.AddRange([Tiles, Files, Visitors]);
        BackColor = Theme.Surface;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Files.Font = Visitors.Font = Font;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int tilesH = Width >= this.S(560) ? this.S(64) : this.S(124);
        Tiles.Bounds = new Rectangle(0, 0, Width, tilesH);
        int top = tilesH + this.S(6), gap = this.S(10);
        int h = Math.Max(0, Height - top);
        int left = (int)((Width - gap) * 0.42);
        Files.Bounds = new Rectangle(0, top, left, h);
        Visitors.Bounds = new Rectangle(left + gap, top, Math.Max(0, Width - left - gap), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Surface);
        int x = Files.Right + this.S(5);
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, x, Files.Top + this.S(4), x, Height - this.S(4));
    }
}

/// <summary>Accounts tab: add row (name, password, Add) on top, account list below.</summary>
sealed class AccountsView : Control
{
    public readonly TextField NameBox = new() { Placeholder = "Name", Backdrop = Theme.Surface };
    public readonly TextField PasswordBox = new() { Placeholder = "Password", Backdrop = Theme.Surface };
    public readonly FlatButton AddButton = new("Add", primary: true) { Backdrop = Theme.Surface };
    public readonly GridList List = new(30) { FillColumn = 0, EmptyText = "No accounts yet" };
    public string Hint { get; set; } = "";

    public AccountsView()
    {
        PasswordBox.Box.UseSystemPasswordChar = true;
        List.AddColumn("Account", 160);
        List.AddColumn("Created", 130);
        List.AddColumn("Last sign-in", 130);
        Controls.AddRange([NameBox, PasswordBox, AddButton, List]);
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        NameBox.Font = PasswordBox.Font = AddButton.Font = List.Font = Font;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = this.S(32), y = this.S(8), x = this.S(8);
        NameBox.Bounds = new Rectangle(x, y, this.S(150), h);
        PasswordBox.Bounds = new Rectangle(NameBox.Right + this.S(8), y, this.S(150), h);
        AddButton.Bounds = new Rectangle(PasswordBox.Right + this.S(8), y, this.S(64), h);
        int top = y + h + this.S(30);
        List.Bounds = new Rectangle(0, top, Width, Math.Max(0, Height - top));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Surface);
        int y = this.S(8) + this.S(32) + this.S(6);
        TextRenderer.DrawText(e.Graphics, Hint, Font, new Rectangle(this.S(10), y, Width - this.S(20), this.S(20)), Theme.SubText,
            Theme.Surface, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

sealed class MenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Hover;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemPressedGradientBegin => Theme.Hover;
    public override Color MenuItemPressedGradientEnd => Theme.Hover;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
}
