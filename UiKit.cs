using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UniversalFrameFX;

public sealed class NavList : Control
{
    public sealed record Item(string Key, string Text);
    readonly List<Item> _items = new();
    int _selected, _hot = -1;
    Font? _selFont;
    bool _keyboardFocus;

    public IList<Item> Items => _items;
    public event EventHandler? SelectedChanged;
    public event EventHandler? EnterPressed;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_items.Count == 0) { _selected = 0; return; }
            int v = Math.Clamp(value, 0, _items.Count - 1);
            if (v == _selected) return;
            _selected = v;
            SelectedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Selection, v);
        }
    }

    public NavList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Font = Theme.Body();
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Pages";
    }

    int RowH => Math.Max(1, (int)(Font.Height * 2.6f));
    Font SelFont => _selFont ??= Theme.Semibold(Font.SizeInPoints);

    protected override void OnFontChanged(EventArgs e)
    {
        _selFont?.Dispose(); _selFont = null;
        base.OnFontChanged(e);
        Fit();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    void Fit() { Height = Math.Max(RowH, _items.Count * RowH); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = RowAt(e.Y);
        if (i != _hot) { _hot = i; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        _keyboardFocus = false;
        Focus();
        int i = RowAt(e.Y);
        if (i >= 0) SelectedIndex = i;
        base.OnMouseDown(e);
    }
    public void FocusFromKeyboard()
    {
        _keyboardFocus = true;
        Focus();
    }
    int RowAt(int y)
    {
        if (_items.Count == 0) return -1;
        int i = y / RowH;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Right or Keys.Enter or Keys.Space || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        _keyboardFocus = true;
        if (_items.Count == 0) { base.OnKeyDown(e); return; }
        if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { SelectedIndex = Math.Min(_items.Count - 1, _selected + 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { SelectedIndex = 0; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { SelectedIndex = _items.Count - 1; e.Handled = true; }
        else if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Right) { EnterPressed?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        _keyboardFocus = true;
        Invalidate();
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Fit();
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        int rh = RowH;
        for (int i = 0; i < _items.Count; i++)
        {
            var row = new Rectangle(0, i * rh, Width, rh);
            if (i == _hot && i != _selected)
            {
                using var hb = new SolidBrush(Theme.Muted);
                g.FillRectangle(hb, row);
            }
            if (i == _selected)
            {
                using var bar = new SolidBrush(Theme.Accent);
                g.FillRectangle(bar, 0, row.Y + (int)(8 * s), (int)Math.Max(2, 3 * s), rh - (int)(16 * s));
            }
            var font = i == _selected ? SelFont : Font;
            var fg = i == _selected ? Theme.Text : Theme.TextMuted;
            TextRenderer.DrawText(g, _items[i].Text, font, new Rectangle(row.X + (int)(12 * s), row.Y, row.Width - (int)(16 * s), row.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (Focused && _keyboardFocus && ShowFocusCues && i == _selected)
            {
                g.SmoothingMode = SmoothingMode.None;
                using var op = new Pen(Theme.Text, 1f);
                g.DrawRectangle(op, row.X, row.Y, Math.Max(0, row.Width - 1), Math.Max(0, row.Height - 1));
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
        }
    }

    sealed class NavAccessible : ControlAccessibleObject
    {
        readonly NavList _n;
        public NavAccessible(NavList n) : base(n) => _n = n;
        public override AccessibleRole Role => AccessibleRole.PageTabList;
        public override int GetChildCount() => _n._items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < _n._items.Count ? new ItemAccessible(_n, index) : null;
    }
    sealed class ItemAccessible : AccessibleObject
    {
        readonly NavList _n;
        readonly int _i;
        public ItemAccessible(NavList n, int i) { _n = n; _i = i; }
        public override string? Name => _i < _n._items.Count ? _n._items[_i].Text : null;
        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Focusable;
                if (_n._selected == _i) s |= AccessibleStates.Selected | AccessibleStates.Focused;
                return s;
            }
        }
        public override AccessibleObject? Parent => _n.AccessibilityObject;
        public override Rectangle Bounds
        {
            get
            {
                int rh = _n.RowH;
                var r = _n.RectangleToScreen(new Rectangle(0, _i * rh, _n.Width, rh));
                return r;
            }
        }
        public override void DoDefaultAction() => _n.SelectedIndex = _i;
    }
}

public sealed class GroupLabel : Label
{
    public GroupLabel(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        AutoSize = false;
        Font = Theme.Caps();
        ForeColor = Theme.TextMuted;
        TabStop = false;
        Padding = Padding.Empty;
        Margin = new Padding(0, 0, 0, Theme.Space.S);
        Height = Font.Height + 4;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Height = Font.Height + 4; }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = Font.Height + 4; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        string s = (Text ?? "").ToUpperInvariant();
        TextRenderer.DrawText(g, s, Font, ClientRectangle, Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

public sealed class Rule : Control
{
    public Rule()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0);
        Fit();
    }
    void Fit() => Height = Math.Max(1, (int)Math.Round(Theme.Scale(this)));
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Rule);
}

public sealed class FieldLabel : Label
{
    public FieldLabel(string text)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.Semibold(10f);
        ForeColor = Theme.Text;
        TabStop = false;
        Padding = Padding.Empty;
        Margin = new Padding(0, 0, 0, Theme.Space.Xs);
    }
}

public sealed class MoreInfo : TableLayoutPanel
{
    readonly MoreLink _link;
    readonly Label _body;
    bool _open;

    public Label Body => _body;
    public bool Open
    {
        get => _open;
        set { if (_open == value) return; _open = value; _body.Visible = value; _link.Invalidate(); PerformLayout(); }
    }

    public MoreInfo(string longText)
    {
        ColumnCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Theme.Bg;
        TabStop = false;
        Margin = new Padding(0, Theme.Space.Xs, 0, 0);
        Padding = Padding.Empty;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body = new Label
        {
            Text = longText,
            AutoSize = true,
            MaximumSize = new Size(200, 0),
            ForeColor = Theme.TextMuted,
            Font = Theme.Small(),
            Visible = false,
            TabStop = false,
            Padding = Padding.Empty,
            Margin = new Padding(0, Theme.Space.Xs, 0, 0),
        };
        _link = new MoreLink(this) { Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        Controls.Add(_link);
        Controls.Add(_body);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _link?.Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); _link?.Invalidate(); }
    protected override void OnSizeChanged(EventArgs e)
    {
        int w = Math.Max(80, Width);
        if (_body != null && _body.MaximumSize.Width != w) _body.MaximumSize = new Size(w, 0);
        base.OnSizeChanged(e);
    }

    void Toggle() => Open = !Open;

    sealed class MoreLink : Control
    {
        readonly MoreInfo _owner;
        bool _hover;
        public MoreLink(MoreInfo owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.Small();
            AccessibleRole = AccessibleRole.Link;
            AccessibleName = "More info";
            string t = owner._body.Text ?? "";
            int cut = t.IndexOfAny(['.', '!', '?']);
            AccessibleDescription = cut >= 0 ? t[..(cut + 1)].Trim() : t;
            Fit();
        }
        void Fit()
        {
            var sz = TextRenderer.MeasureText("Less info", Font);
            Size = new Size(sz.Width + 8, Math.Max(sz.Height + 4, Font.Height + 4));
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); _owner.Toggle(); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { _owner.Toggle(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            string t = _owner._open ? "Less info" : "More info";
            bool und = _hover || Focused;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, t, Font, ClientRectangle, Theme.TextMuted, flags);
            if (und)
            {
                var sz = TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int y = (Height + sz.Height) / 2;
                using var p = new Pen(Theme.TextMuted);
                g.DrawLine(p, 0, y, sz.Width, y);
            }
            if (Focused && ShowFocusCues) Theme.DrawFocus(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Scale(this));
        }
    }
}

public sealed class SettingRow : TableLayoutPanel
{
    public Control Control { get; }

    public SettingRow(Control control, string? label, string? oneLiner, string? more)
    {
        Control = control;
        ColumnCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Theme.Bg;
        TabStop = false;
        Margin = new Padding(0, 0, 0, Theme.Space.M);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (!string.IsNullOrEmpty(label))
            Controls.Add(new FieldLabel(label));
        control.Margin = new Padding(0);
        var host = Wrap(control);
        host.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        host.Margin = new Padding(0);
        Controls.Add(host);
        if (!string.IsNullOrEmpty(oneLiner))
        {
            var d = new Label
            {
                Text = oneLiner,
                AutoSize = true,
                MaximumSize = new Size(200, 0),
                ForeColor = Theme.TextMuted,
                Font = Theme.Small(),
                TabStop = false,
                Padding = Padding.Empty,
                Margin = new Padding(0, Theme.Space.Xs, 0, 0),
            };
            Controls.Add(d);
        }
        if (!string.IsNullOrEmpty(more))
            Controls.Add(new MoreInfo(more) { Margin = new Padding(0, Theme.Space.Xs, 0, 0) });
        string name = label ?? (control as CheckBox)?.Text ?? control.Text;
        if (!string.IsNullOrEmpty(name)) control.AccessibleName = name;
        if (!string.IsNullOrEmpty(oneLiner)) control.AccessibleDescription = oneLiner;
    }

    static Control Wrap(Control c)
    {
        if (c is not TextBox tb) return c;
        var frame = new Panel
        {
            Padding = new Padding(1),
            BackColor = Theme.Rule,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = tb.Height + 2,
        };
        tb.BorderStyle = BorderStyle.None;
        tb.Dock = DockStyle.Fill;
        tb.Enter += (_, _) => frame.BackColor = Theme.Text;
        tb.Leave += (_, _) => frame.BackColor = Theme.Rule;
        frame.Controls.Add(tb);
        frame.Resize += (_, _) => { if (tb.Multiline) tb.Height = Math.Max(tb.Font.Height * 4, frame.ClientSize.Height); };
        return frame;
    }
}

public sealed class PageView : TableLayoutPanel
{
    public string Key { get; }
    readonly int _headerCount;

    public PageView(string key, string title, string? subtitle)
    {
        Key = key;
        ColumnCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0);
        BackColor = Theme.Bg;
        TabStop = false;
        Padding = new Padding(Theme.Space.Xxl, Theme.Space.Xl, Theme.Space.Xxl, Theme.Space.Xxl);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var t = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Title(),
            ForeColor = Theme.Text,
            TabStop = false,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, subtitle == null ? Theme.Space.L : Theme.Space.Xs),
        };
        Controls.Add(t);
        _headerCount = 1;
        if (!string.IsNullOrEmpty(subtitle))
        {
            Controls.Add(new Label
            {
                Text = subtitle,
                AutoSize = true,
                MaximumSize = new Size(200, 0),
                Font = Theme.Body(),
                ForeColor = Theme.TextMuted,
                TabStop = false,
                Padding = Padding.Empty,
                Tag = "fill",
                Margin = new Padding(0, 0, 0, Theme.Space.L),
            });
            _headerCount = 2;
        }
    }

    internal static Rule GroupRule() => new() { Margin = new Padding(0, Theme.Space.Xl, 0, Theme.Space.L) };

    static int TrailingBottom(Control c)
    {
        if (c is TableLayoutPanel tlp)
        {
            Control? last = null;
            foreach (Control ch in tlp.Controls)
                if (ch.Visible) last = ch;
            if (last != null) return Math.Max(c.Margin.Bottom, TrailingBottom(last));
        }
        return c.Margin.Bottom;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        Control? prev = null;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            Rule? rule = c as Rule;
            if (rule == null && c is TableLayoutPanel tlp)
            {
                foreach (Control ch in tlp.Controls)
                {
                    if (!ch.Visible) continue;
                    rule = ch as Rule;
                    break;
                }
            }
            if (rule != null && prev != null)
            {
                int top = Math.Max(0, Theme.Space.Xl - TrailingBottom(prev));
                var m = rule.Margin;
                if (m.Top != top || m.Bottom != Theme.Space.L)
                    rule.Margin = new Padding(m.Left, top, m.Right, Theme.Space.L);
            }
            prev = c;
        }
        base.OnLayout(levent);
    }

    public TableLayoutPanel Group(string caps)
    {
        if (Controls.Count > _headerCount)
            Controls.Add(GroupRule());
        Controls.Add(new GroupLabel(caps));
        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
            BackColor = Theme.Bg,
            TabStop = false,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(body);
        return body;
    }

    public (SectionHeader head, TableLayoutPanel body) Disclosure(string text, bool open)
    {
        var head = new SectionHeader(text) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, Theme.Space.M, 0, 0), Open = open, BackColor = Theme.Bg };
        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(0, Theme.Space.S, 0, Theme.Space.Xs),
            Margin = new Padding(0),
            Visible = open,
            BackColor = Theme.Bg,
            TabStop = false,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(head);
        Controls.Add(body);
        return (head, body);
    }
}

public sealed class StatGrid : TableLayoutPanel
{
    int _row;
    public StatGrid()
    {
        ColumnCount = 2;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        BackColor = Theme.Bg;
        TabStop = false;
        Margin = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
    }
    public Label Add(string caption)
    {
        var cap = new Label
        {
            Text = caption,
            AutoSize = true,
            Font = Theme.Small(),
            ForeColor = Theme.TextMuted,
            TabStop = false,
            Margin = new Padding(0, 0, Theme.Space.S, Theme.Space.M),
        };
        var val = new Label
        {
            Text = "–",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = Theme.Body(12f),
            ForeColor = Theme.Text,
            TabStop = false,
            Margin = new Padding(0, 0, 0, Theme.Space.M),
        };
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(cap, 0, _row);
        Controls.Add(val, 1, _row);
        _row++;
        return val;
    }
}
