using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UniversalFrameFX;

public static class Theme
{
    public static readonly bool IsLight;
    public static readonly Color Bg;
    public static readonly Color Side;
    public static readonly Color AccentSoft;
    public static readonly Color Card;
    public static readonly Color CardBorder;
    public static readonly Color Border;
    public static readonly Color Rule;
    public static readonly Color Input;
    public static readonly Color InputHover;
    public static readonly Color Muted;
    public static readonly Color Text;
    public static readonly Color TextMuted;
    public static readonly Color TextDim;
    public static readonly Color Accent;
    public static readonly Color Primary;
    public static readonly Color Focus;
    public static readonly Color PrimaryHover;
    public static readonly Color PrimaryText;
    public static readonly Color Green;
    public static readonly Color Amber;
    public static readonly Color Red;

    static Theme()
    {
        var env = Environment.GetEnvironmentVariable("UFX_THEME");
        if (string.Equals(env, "light", StringComparison.OrdinalIgnoreCase)) IsLight = true;
        else if (string.Equals(env, "dark", StringComparison.OrdinalIgnoreCase)) IsLight = false;
        else IsLight = AppsUseLightTheme();

        // Soft warm neutrals with a single periwinkle accent. Cards sit one step off the page colour,
        // the sidebar one step the other way, so structure reads without heavy borders.
        if (IsLight)
        {
            Bg = Color.FromArgb(0xf6, 0xf5, 0xf2);
            Side = Color.FromArgb(0xef, 0xed, 0xe8);
            Card = Color.White;
            CardBorder = Color.FromArgb(0xe7, 0xe4, 0xde);
            Border = Color.FromArgb(0xdc, 0xd8, 0xd0);
            Rule = Color.FromArgb(0xe8, 0xe5, 0xdf);
            Input = Color.White;
            InputHover = Color.FromArgb(0xf3, 0xf1, 0xec);
            Muted = Color.FromArgb(0xe9, 0xe6, 0xe0);
            Text = Color.FromArgb(0x1d, 0x1c, 0x1a);
            TextMuted = Color.FromArgb(0x6b, 0x68, 0x62);
            TextDim = Color.FromArgb(0xa9, 0xa5, 0x9d);
            Accent = Primary = Focus = Color.FromArgb(0x5b, 0x5b, 0xd6);
            AccentSoft = Color.FromArgb(0xe6, 0xe6, 0xfa);
            PrimaryHover = Color.FromArgb(0x4d, 0x4d, 0xc6);
            PrimaryText = Color.White;
            Green = Color.FromArgb(0x2e, 0x6b, 0x3a);
            Amber = Color.FromArgb(0x8a, 0x53, 0x00);
            Red = Color.FromArgb(0xb4, 0x23, 0x18);
        }
        else
        {
            Bg = Color.FromArgb(0x13, 0x13, 0x12);
            Side = Color.FromArgb(0x18, 0x18, 0x17);
            Card = Color.FromArgb(0x1c, 0x1c, 0x1b);
            CardBorder = Color.FromArgb(0x27, 0x26, 0x24);
            Border = Color.FromArgb(0x33, 0x32, 0x2f);
            Rule = Color.FromArgb(0x26, 0x25, 0x23);
            Input = Color.FromArgb(0x22, 0x21, 0x20);
            InputHover = Color.FromArgb(0x29, 0x28, 0x26);
            Muted = Color.FromArgb(0x25, 0x24, 0x22);
            Text = Color.FromArgb(0xed, 0xeb, 0xe7);
            TextMuted = Color.FromArgb(0x9e, 0x9a, 0x93);
            TextDim = Color.FromArgb(0x5c, 0x59, 0x54);
            Accent = Primary = Focus = Color.FromArgb(0x8b, 0x8d, 0xf2);
            AccentSoft = Color.FromArgb(0x26, 0x26, 0x3a);
            PrimaryHover = Color.FromArgb(0x9e, 0xa0, 0xf5);
            PrimaryText = Color.FromArgb(0x13, 0x13, 0x12);
            Green = Color.FromArgb(0x8f, 0xbf, 0x98);
            Amber = Color.FromArgb(0xe0, 0xa6, 0x4a);
            Red = Color.FromArgb(0xe5, 0x67, 0x5a);
        }
    }

    static bool AppsUseLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = k?.GetValue("AppsUseLightTheme");
            if (v is int i) return i != 0;
            if (v is long l) return l != 0;
        }
        catch { }
        return false;
    }

    public static class Space
    {
        public const int Xs = 4, S = 8, M = 12, L = 16, Xl = 24, Xxl = 32;
    }

    public static class Radius
    {
        public const float Control = 8, Card = 14;
    }

    static bool Has(string name)
    {
        try { using var f = new FontFamily(name); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    static string? _family, _display;
    public static string Family => _family ??= Has("Inter") ? "Inter" : Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static string DisplayFamily => _display ??= Has("Segoe UI Variable Display") ? "Segoe UI Variable Display" : Family;
    public static Font Body(float pt = 10f, FontStyle st = FontStyle.Regular) => new(Family, pt, st);
    public static Font Semibold(float pt) => new(Family == "Segoe UI" ? "Segoe UI Semibold" : Family, pt, Family == "Segoe UI" ? FontStyle.Regular : FontStyle.Bold);
    public static Font Title() => Semibold(18f);
    public static Font Small() => Body(9f);
    public static Font Caps() => Semibold(8.5f);

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

    public static void DrawFocus(Graphics g, RectangleF r, float s)
    {
        var inset = RectangleF.Inflate(r, -1f * s, -1f * s);
        if (inset.Width < 2 || inset.Height < 2) return;
        using var path = Round(inset, Radius.Control * s);
        using var p = new Pen(Accent, 2f * s);
        g.DrawPath(p, path);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void DarkChrome(Form f)
    {
        int immersive = IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(f.Handle, 20, ref immersive, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref immersive, 4);
        int none = 1; DwmSetWindowAttribute(f.Handle, 38, ref none, 4);
        int caption = ColorTranslator.ToWin32(Bg); DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
        int text = ColorTranslator.ToWin32(Text); DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
        int border = ColorTranslator.ToWin32(Rule); DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
    }

    public static void DarkNative(Control c, string theme = "DarkMode_Explorer")
    {
        if (IsLight)
        {
            if (theme == "DarkMode_Explorer") theme = "Explorer";
            else if (theme == "DarkMode_CFD") theme = "CFD";
        }
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
        Font = Theme.Semibold(10f);
        Cursor = Cursors.Hand;
        AutoSize = false;
        Margin = new Padding(0, 0, 8, 0);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text.Replace("&&", "&"), Font);
        int h = Math.Max((int)(Font.Height * 2.4f), t.Height + 8);
        int w = t.Width + 32;
        return new Size(Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
    }

    void FitAuto() { if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnCreateControl() { base.OnCreateControl(); FitAuto(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitAuto(); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitAuto(); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        var r = new RectangleF(1f * s, 1f * s, Width - 2f * s, Height - 2f * s);
        Color fill, fg;
        bool drawBorder = false;
        bool ring = _ring;
        if (!Enabled)
        {
            fill = Color.Transparent;
            fg = Theme.TextDim;
            drawBorder = true;
            ring = false;
        }
        else switch (_kind)
        {
            case PillKind.Primary:
                fill = _down || _hover ? Theme.PrimaryHover : Theme.Primary;
                fg = Theme.PrimaryText;
                break;
            case PillKind.Ghost:
                fill = _down || _hover ? Theme.Muted : Color.Transparent;
                fg = Theme.Text;
                break;
            default:
                fill = _down || _hover ? Theme.Muted : Color.Transparent;
                fg = Theme.Text;
                drawBorder = true;
                break;
        }
        using (var path = Theme.Round(r, r.Height / 2))
        {
            using var b = new SolidBrush(fill); g.FillPath(b, path);
            if (_kind != PillKind.Primary && ring)
            {
                using var p = new Pen(Theme.Accent, 1f * s); g.DrawPath(p, path);
            }
            else if (drawBorder)
            {
                using var p = new Pen(Theme.Rule, 1f * s); g.DrawPath(p, path);
            }
            if (_kind == PillKind.Primary && ring)
            {
                using var p = new Pen(Theme.PrimaryText, 2f * s);
                float y = r.Bottom - 5 * s;
                g.DrawLine(p, r.X + 8 * s, y, r.Right - 8 * s, y);
            }
            if (Focused && ShowFocusCues) Theme.DrawFocus(g, r, s);
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
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
    }
}

public class SectionHeader : Control
{
    bool _open, _hover;
    public bool Open { get => _open; set { _open = value; Invalidate(); } }
    public SectionHeader(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text; Font = Theme.Caps(); Cursor = Cursors.Hand; TabStop = true;
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
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        float cy = Height / 2f, cx = Width - 16 * s, a = 4.5f * s;
        using (var p = new Pen(_hover ? Theme.Text : Theme.TextMuted, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (_open) g.DrawLines(p, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
            else g.DrawLines(p, new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) });
        }
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), Font, new Rectangle(0, 0, Width - (int)(28 * s), Height), Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        if (!_open)
        {
            using var rp = new Pen(Theme.Rule, Math.Max(1f, 1f * s));
            g.DrawLine(rp, 0, Height - 1, Width, Height - 1);
        }
        if (Focused && ShowFocusCues) Theme.DrawFocus(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), s);
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
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int v = _val;
        if (e.KeyCode == Keys.Left) v -= SmallChange;
        else if (e.KeyCode == Keys.Right) v += SmallChange;
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
        float s = Theme.Scale(this), cy = Height / 2f, th = 2 * s;
        float t = _max > _min ? (float)(_val - _min) / (_max - _min) : 0;
        float x0 = Pad, x1 = Width - Pad, xv = x0 + t * (x1 - x0);
        using (var p = Theme.Round(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2)) { using var b = new SolidBrush(Theme.Rule); g.FillPath(b, p); }
        if (xv > x0 + 1) using (var p = Theme.Round(new RectangleF(x0, cy - th / 2, xv - x0, th), th / 2)) { using var b = new SolidBrush(Theme.Text); g.FillPath(b, p); }
        float side = 12 * s;
        var thumb = new RectangleF(xv - side / 2, cy - side / 2, side, side);
        using (var path = Theme.Round(thumb, 2 * s)) { using var b = new SolidBrush(Theme.Text); g.FillPath(b, path); }
        if (Focused && ShowFocusCues) Theme.DrawFocus(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), s);
        _ = _hover || _drag;
    }
}

public class ToggleSwitch : CheckBox
{
    bool _hover;
    readonly Glide _knob;
    const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false; Cursor = Cursors.Hand; Font = Theme.Body();
        _knob = new Glide(this, 0f);
    }
    protected override void OnCheckedChanged(EventArgs e) { _knob.To(Checked ? 1f : 0f, Created && Visible); base.OnCheckedChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) _knob.Dispose(); base.Dispose(disposing); }
    int TextX
    {
        get { float s = Theme.Scale(this); return (int)(32 * s + 8 * s); }
    }
    int HeightFor(int width)
    {
        float s = Theme.Scale(this);
        int tw = Math.Max(1, width - TextX);
        var sz = TextRenderer.MeasureText(Text, Font, new Size(tw, int.MaxValue), TextFlags);
        return Math.Max((int)(34 * s), sz.Height + (int)(8 * s));
    }
    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        float s = Theme.Scale(this);
        int w = (int)(32 * s) + t.Width + 12;
        if (MaximumSize.Width > 0) w = Math.Min(w, MaximumSize.Width);
        return new Size(w, HeightFor(w));
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Size = GetPreferredSize(Size.Empty); }
    protected override void OnCreateControl() { base.OnCreateControl(); Size = GetPreferredSize(Size.Empty); }
    protected override void OnSizeChanged(EventArgs e)
    {
        int h = HeightFor(Width);
        if (Height != h) Height = h;
        base.OnSizeChanged(e);
    }
    protected override bool IsInputKey(Keys k) => k == Keys.Enter || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Theme.Scale(this);
        int tx = TextX;
        int textW = Math.Max(1, Width - tx);
        var textSz = TextRenderer.MeasureText(Text, Font, new Size(textW, int.MaxValue), TextFlags);
        int textH = textSz.Height;
        int top = Math.Max(0, (Height - textH) / 2);
        float w = 32 * s, h = 18 * s, x = 2 * s;
        float y = top + (Font.Height - h) / 2f;
        var track = new RectangleF(x, y, w, h);
        float k = _knob.Value;
        var off = _hover ? Theme.InputHover : Theme.Input;
        using (var p = Theme.Round(track, h / 2))
        {
            using var b = new SolidBrush(Enabled ? Glide.Mix(off, Theme.Accent, k) : off); g.FillPath(b, p);
            if (k < 0.99f) { using var bp = new Pen(Glide.Mix(Theme.Border, Theme.Accent, k), 1f * s); g.DrawPath(bp, p); }
        }
        float d = 12 * s, x0 = track.X + 3 * s, x1 = track.Right - d - 3 * s, kx = x0 + (x1 - x0) * k, ky = y + (h - d) / 2;
        var knob = Enabled ? Glide.Mix(Theme.TextMuted, Theme.PrimaryText, k) : Theme.TextDim;
        using (var b = new SolidBrush(knob)) g.FillEllipse(b, kx, ky, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, top, textW, textH), Enabled ? Theme.Text : Theme.TextDim, TextFlags);
        if (Focused && ShowFocusCues) Theme.DrawFocus(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), s);
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
        ItemHeight = (int)(Font.Height * 1.9f);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme.DarkNative(this, "DarkMode_CFD");
        Cursor = Cursors.Hand;
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = (int)(Font.Height * 1.9f); }
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
    protected override bool IsInputKey(Keys keyData)
    {
        var k = keyData & Keys.KeyCode;
        if (DroppedDown) return k is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape || base.IsInputKey(keyData);
        if (k is Keys.Up or Keys.Down) return false;
        if (k is Keys.Enter or Keys.Space or Keys.F4) return true;
        return base.IsInputKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!DroppedDown && e.KeyCode is Keys.Enter or Keys.Space or Keys.F4)
        {
            DroppedDown = true;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using var b = new SolidBrush(sel ? Theme.Muted : Theme.Card);
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
        using (var p = Theme.Round(r, Theme.Radius.Control * s))
        {
            using var b = new SolidBrush(_hover ? Theme.InputHover : Theme.Input); g.FillPath(b, p);
            Color bc = Focused || DroppedDown ? Theme.Text : Theme.Rule;
            using var pen = new Pen(bc, 1f * s); g.DrawPath(pen, p);
        }
        float cx = Width - 16 * s, cy = Height / 2f, a = 4f * s;
        using (var pen = new Pen(Theme.TextMuted, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
        var tr = new Rectangle((int)(12 * s), 0, Width - (int)(36 * s), Height);
        TextRenderer.DrawText(g, SelectedItem != null ? GetItemText(SelectedItem) : "", Font, tr, Enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues && !DroppedDown) Theme.DrawFocus(g, r, s);
    }
}
