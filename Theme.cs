using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UniversalFrameFX;

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(9, 9, 11);
    public static readonly Color Card = Color.FromArgb(17, 17, 20);
    public static readonly Color CardBorder = Color.FromArgb(29, 29, 33);
    public static readonly Color Border = Color.FromArgb(36, 36, 40);
    public static readonly Color Input = Color.FromArgb(43, 43, 48);
    public static readonly Color InputHover = Color.FromArgb(52, 52, 58);
    public static readonly Color Muted = Color.FromArgb(29, 29, 33);
    public static readonly Color Text = Color.FromArgb(242, 242, 242);
    public static readonly Color TextMuted = Color.FromArgb(138, 138, 147);
    public static readonly Color TextDim = Color.FromArgb(88, 88, 96);
    public static readonly Color Primary = Color.FromArgb(237, 237, 237);
    public static readonly Color PrimaryHover = Color.FromArgb(255, 255, 255);
    public static readonly Color PrimaryText = Color.FromArgb(9, 9, 11);
    public static readonly Color Green = Color.FromArgb(51, 204, 128);
    public static readonly Color Amber = Color.FromArgb(244, 191, 38);
    public static readonly Color Red = Color.FromArgb(207, 48, 48);

    static bool Has(string name)
    {
        try { using var f = new FontFamily(name); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    static string? _family, _display;
    public static string Family => _family ??= Has("Inter") ? "Inter" : Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string DisplayFamily => _display ??= Has("Space Grotesk") ? "Space Grotesk" : Has("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI Semibold";
    public static Font Body(float pt = 9.75f, FontStyle st = FontStyle.Regular) => new(Family, pt, st);
    public static Font Semibold(float pt) => new(Family == "Segoe UI" ? "Segoe UI Semibold" : Family, pt, Family == "Segoe UI" ? FontStyle.Regular : FontStyle.Bold);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static float Scale(Control c) => c.DeviceDpi / 96f;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void DarkChrome(Form f)
    {
        int on = 1;
        if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
        int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
        int mica = 2; DwmSetWindowAttribute(f.Handle, 38, ref mica, 4);
        int caption = ColorTranslator.ToWin32(Bg); DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
        int text = ColorTranslator.ToWin32(Text); DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
        int border = ColorTranslator.ToWin32(Border); DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
    }

    public static void DarkNative(Control c, string theme = "DarkMode_Explorer")
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null);
        else c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, theme, null);
    }
}

public enum PillKind { Primary, Secondary, Ghost }

public class PillButton : Button
{
    PillKind _kind = PillKind.Secondary;
    bool _hover, _down, _ring;
    [DefaultValue(PillKind.Secondary)] public PillKind Kind { get => _kind; set { _kind = value; Invalidate(); } }
    [DefaultValue(false)] public bool Ring { get => _ring; set { _ring = value; Invalidate(); } }

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        Font = Theme.Semibold(9.75f);
        Cursor = Cursors.Hand;
        AutoSize = false;
        Margin = new Padding(0, 0, 8, 0);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text.Replace("&&", "&"), Font);
        int h = (int)(Font.Height * 2.3f);
        return new Size(t.Width + (int)(Font.Height * 2.2f), h);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        var r = new RectangleF(1.5f * s, 1.5f * s, Width - 3f * s, Height - 3f * s);
        Color fill, fg, border;
        switch (_kind)
        {
            case PillKind.Primary:
                fill = !Enabled ? Theme.Input : _down ? Color.FromArgb(210, 210, 210) : _hover ? Theme.PrimaryHover : Theme.Primary;
                fg = Enabled ? Theme.PrimaryText : Theme.TextMuted; border = fill; break;
            case PillKind.Ghost:
                fill = _down ? Theme.Input : _hover ? Theme.Muted : (Parent?.BackColor ?? Theme.Bg);
                fg = Enabled ? Theme.Text : Theme.TextMuted; border = Theme.Border; break;
            default:
                fill = !Enabled ? Theme.Card : _down ? Theme.InputHover : _hover ? Theme.Input : Theme.Muted;
                fg = Enabled ? Theme.Text : Theme.TextMuted; border = Theme.Border; break;
        }
        using (var path = Theme.Round(r, r.Height / 2))
        {
            using var b = new SolidBrush(fill); g.FillPath(b, path);
            using var p = new Pen(_ring ? Theme.Amber : border, (_ring ? 2f : 1f) * s); g.DrawPath(p, path);
            if (Focused && ShowFocusCues)
            {
                var fr = RectangleF.Inflate(r, -3 * s, -3 * s);
                using var fp = Theme.Round(fr, fr.Height / 2);
                using var pp = new Pen(Color.FromArgb(120, _kind == PillKind.Primary ? Theme.PrimaryText : Theme.Text), 1f * s) { DashStyle = DashStyle.Dot };
                g.DrawPath(pp, fp);
            }
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

public class CardPanel : TableLayoutPanel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, 14 * s);
        using var b = new SolidBrush(Theme.Card); g.FillPath(b, path);
        using var p = new Pen(Theme.CardBorder, 1f * s); g.DrawPath(p, path);
    }
}

public class SectionHeader : Control
{
    bool _open, _hover;
    public bool Open { get => _open; set { _open = value; Invalidate(); } }
    public SectionHeader(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text; Font = Theme.Semibold(11f); Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton; AccessibleName = text + " section";
        Height = (int)(Font.Height * 2.2f);
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Height = (int)(Font.Height * 2.2f); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Space or Keys.Enter || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        float cy = Height / 2f, cx = Width - 16 * s, a = 4.5f * s;
        using (var p = new Pen(_hover ? Theme.Text : Theme.TextMuted, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (_open) g.DrawLines(p, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
            else g.DrawLines(p, new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(2 * s), 0, Width, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(Theme.TextMuted, 1f * s) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(fp, 0, 2, Width - 1, Height - 5);
        }
    }
}

public class FlatSlider : Control
{
    int _min, _max = 100, _val;
    bool _drag, _hover;
    public event EventHandler? Scroll;
    public int Minimum { get => _min; set { _min = value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; Invalidate(); } }
    public int Value { get => _val; set { int v = Math.Clamp(value, _min, _max); if (v != _val) { _val = v; Invalidate(); } } }
    public int SmallChange { get; set; } = 5;
    public FlatSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider;
        Font = Theme.Body();
        Height = Font.Height * 2;
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Height = Font.Height * 2; }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = Font.Height * 2; }
    float Pad => 10 * Theme.Scale(this);
    void SetFromX(int x)
    {
        float t = Math.Clamp((x - Pad) / Math.Max(1, Width - 2 * Pad), 0, 1);
        int v = (int)Math.Round(_min + t * (_max - _min));
        if (v != _val) { _val = v; Invalidate(); Scroll?.Invoke(this, EventArgs.Empty); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); _drag = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int v = _val;
        if (e.KeyCode is Keys.Left or Keys.Down) v -= SmallChange;
        else if (e.KeyCode is Keys.Right or Keys.Up) v += SmallChange;
        else if (e.KeyCode == Keys.Home) v = _min; else if (e.KeyCode == Keys.End) v = _max;
        v = Math.Clamp(v, _min, _max);
        if (v != _val) { _val = v; Invalidate(); Scroll?.Invoke(this, EventArgs.Empty); }
        base.OnKeyDown(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this), cy = Height / 2f, th = 4 * s;
        float t = _max > _min ? (float)(_val - _min) / (_max - _min) : 0;
        float x0 = Pad, x1 = Width - Pad, xv = x0 + t * (x1 - x0);
        using (var p = Theme.Round(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2)) { using var b = new SolidBrush(Theme.Input); g.FillPath(b, p); }
        if (xv > x0 + 1) using (var p = Theme.Round(new RectangleF(x0, cy - th / 2, xv - x0, th), th / 2)) { using var b = new SolidBrush(Theme.Primary); g.FillPath(b, p); }
        float r = (_hover || _drag || Focused ? 8f : 7f) * s;
        using (var b = new SolidBrush(Theme.Primary)) g.FillEllipse(b, xv - r, cy - r, 2 * r, 2 * r);
        using (var p = new Pen(Theme.Bg, 2f * s)) g.DrawEllipse(p, xv - r, cy - r, 2 * r, 2 * r);
        if (Focused && ShowFocusCues) using (var p = new Pen(Color.FromArgb(90, Theme.Primary), 2f * s)) g.DrawEllipse(p, xv - r - 3 * s, cy - r - 3 * s, 2 * r + 6 * s, 2 * r + 6 * s);
    }
}

public class ToggleSwitch : CheckBox
{
    bool _hover;
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false; Cursor = Cursors.Hand; Font = Theme.Body();
    }
    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size((int)(Font.Height * 2.9f) + t.Width + 8, (int)(Font.Height * 1.9f));
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Size = GetPreferredSize(Size.Empty); }
    protected override void OnCreateControl() { base.OnCreateControl(); Size = GetPreferredSize(Size.Empty); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        float h = Font.Height * 1.15f, w = h * 1.8f, y = (Height - h) / 2f, x = 2 * s;
        var track = new RectangleF(x, y, w, h);
        using (var p = Theme.Round(track, h / 2))
        {
            using var b = new SolidBrush(Checked ? Theme.Green : (_hover ? Theme.InputHover : Theme.Input)); g.FillPath(b, p);
            if (Focused && ShowFocusCues) { using var fp = new Pen(Color.FromArgb(140, Theme.Text), 1.5f * s); g.DrawPath(fp, p); }
        }
        float d = h - 5 * s, kx = Checked ? track.Right - d - 2.5f * s : track.X + 2.5f * s;
        using (var b = new SolidBrush(Enabled ? Color.White : Theme.TextMuted)) g.FillEllipse(b, kx, y + 2.5f * s, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(x + w + 8 * s), 0, Width, Height), Enabled ? Theme.Text : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

public class DarkCombo : ComboBox
{
    bool _hover;
    public DarkCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Input; ForeColor = Theme.Text; Font = Theme.Body();
        ItemHeight = (int)(Font.Height * 1.6f);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme.DarkNative(this, "DarkMode_CFD");
        Cursor = Cursors.Hand;
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = (int)(Font.Height * 1.6f); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    public Func<int, string?>? DisabledReason { get; set; }
    readonly ToolTip _tip = new() { InitialDelay = 200, AutoPopDelay = 8000 };
    int _lastValid = -1, _tipIndex = -1;
    public string? ReasonFor(int i) => i >= 0 ? DisabledReason?.Invoke(i) : null;
    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (ReasonFor(SelectedIndex) is { } why)
        {
            int back = _lastValid >= 0 && _lastValid < Items.Count ? _lastValid : 0;
            _tip.Show(why, this, 0, Height + 2, 4000);
            BeginInvoke(new Action(() => SelectedIndex = back));
            return;
        }
        _lastValid = SelectedIndex;
        Invalidate(); base.OnSelectedIndexChanged(e);
    }
    protected override void OnDropDownClosed(EventArgs e) { _tip.Hide(this); _tipIndex = -1; base.OnDropDownClosed(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using var b = new SolidBrush(sel ? Theme.InputHover : Theme.Card);
        e.Graphics.FillRectangle(b, e.Bounds);
        var r = e.Bounds; r.X += (int)(10 * Theme.Scale(this)); r.Width -= (int)(10 * Theme.Scale(this));
        var why = ReasonFor(e.Index);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, why != null ? Theme.TextDim : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (sel && DroppedDown && why != null && _tipIndex != e.Index)
        {
            _tipIndex = e.Index;
            _tip.Show(why, this, Width + 4, Height + e.Bounds.Y, 5000);
        }
        else if (sel && why == null && _tipIndex >= 0) { _tip.Hide(this); _tipIndex = -1; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var p = Theme.Round(r, 10 * s))
        {
            using var b = new SolidBrush(_hover || DroppedDown ? Theme.InputHover : Theme.Input); g.FillPath(b, p);
            using var pen = new Pen(Focused ? Theme.TextMuted : Theme.Border, 1f * s); g.DrawPath(pen, p);
        }
        float cx = Width - 16 * s, cy = Height / 2f, a = 4f * s;
        using (var pen = new Pen(Theme.TextMuted, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
        var tr = new Rectangle((int)(12 * s), 0, Width - (int)(36 * s), Height);
        TextRenderer.DrawText(g, SelectedItem != null ? GetItemText(SelectedItem) : "", Font, tr, Enabled ? Theme.Text : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
