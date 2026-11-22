using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AioLite;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(14, 15, 20);
    public static readonly Color Panel = Color.FromArgb(22, 24, 31);
    public static readonly Color PanelHover = Color.FromArgb(30, 33, 42);
    public static readonly Color Border = Color.FromArgb(40, 43, 54);
    public static readonly Color Text = Color.FromArgb(236, 237, 242);
    public static readonly Color Muted = Color.FromArgb(140, 145, 160);
    public static readonly Color Accent = Color.FromArgb(139, 92, 246);
    public static readonly Color AccentSoft = Color.FromArgb(60, 139, 92, 246);
    public static readonly Color Green = Color.FromArgb(52, 211, 153);
    public static readonly Color Orange = Color.FromArgb(251, 191, 36);
    public static readonly Color Red = Color.FromArgb(248, 113, 113);

    static readonly string Family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
    public static readonly Font Title = new(Family, 17f, FontStyle.Bold);
    public static readonly Font H2 = new(Family, 11f, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9.75f);
    public static readonly Font Small = new(Family, 8.75f);

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, 4);                 // dark mode
        int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, 4);           // rounded corners (win11)
        int cap = ColorTranslator.ToWin32(Bg); DwmSetWindowAttribute(hwnd, 35, ref cap, 4); // couleur de la barre du haut
    }
}

// base pour mes controles custom (double buffer sinon ca clignote)
class Smooth : Control
{
    public Smooth()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.Body;
    }
    protected bool Hover { get; private set; }
    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected static void Hq(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; }
}

// panel avec coins arrondis
class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg; Padding = new Padding(18);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
        using var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14);
        using var b = new SolidBrush(Theme.Panel);
        using var pen = new Pen(Theme.Border);
        e.Graphics.FillPath(b, p);
        e.Graphics.DrawPath(pen, p);
    }
}

class FlatButton : Smooth
{
    public bool Primary { get; set; }
    public FlatButton() { Cursor = Cursors.Hand; Height = 34; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        var bg = Primary ? (Hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent) : (Hover ? Theme.PanelHover : Theme.Panel);
        using var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9);
        using var b = new SolidBrush(bg);
        g.FillPath(b, p);
        if (!Primary) { using var pen = new Pen(Theme.Border); g.DrawPath(pen, p); }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class Toggle : Smooth
{
    bool _on;
    public event EventHandler? Changed;
    public bool On { get => _on; set { if (_on == value) return; _on = value; Invalidate(); } }
    public Toggle() { Size = new Size(44, 24); Cursor = Cursors.Hand; }
    protected override void OnClick(EventArgs e) { On = !On; Changed?.Invoke(this, e); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using var p = Theme.Round(r, r.Height / 2);
        using var b = new SolidBrush(_on ? Theme.Accent : Theme.Border);
        g.FillPath(b, p);
        float d = r.Height - 6, x = _on ? r.Right - d - 3 : r.X + 3;
        using var k = new SolidBrush(Color.White);
        g.FillEllipse(k, x, r.Y + 3, d, d);
    }
}

// le truc a 2 boutons genre Ajuster / Remplir
class Segmented : Smooth
{
    public string[] Items { get; set; } = [];
    int _sel;
    public event EventHandler? Changed;
    public int Selected { get => _sel; set { _sel = value; Invalidate(); } }
    public Segmented() { Height = 32; Cursor = Cursors.Hand; }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Items.Length == 0) return;
        int i = Math.Clamp(e.X * Items.Length / Width, 0, Items.Length - 1);
        if (i != _sel) { _sel = i; Invalidate(); Changed?.Invoke(this, e); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9))
        using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, p);
        if (Items.Length == 0) return;
        float w = (Width - 6f) / Items.Length;
        for (int i = 0; i < Items.Length; i++)
        {
            var r = new RectangleF(3 + i * w, 3, w, Height - 6);
            if (i == _sel)
            {
                using var p = Theme.Round(r, 7);
                using var b = new SolidBrush(Theme.Accent);
                g.FillPath(b, p);
            }
            TextRenderer.DrawText(g, Items[i], Font, Rectangle.Round(r), i == _sel ? Color.White : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

class Slider : Smooth
{
    int _value = 50;
    bool _drag;
    public int Min { get; set; }
    public int Max { get; set; } = 100;
    public event EventHandler? ValueChanged;   // while dragging
    public event EventHandler? Committed;      // quand on lache la souris
    public int Value { get => _value; set { _value = Math.Clamp(value, Min, Max); Invalidate(); } }
    public Slider() { Height = 26; Cursor = Cursors.Hand; }
    void SetFromX(int x)
    {
        int v = Min + (int)Math.Round((x - 10.0) / Math.Max(1, Width - 20) * (Max - Min));
        v = Math.Clamp(v, Min, Max);
        if (v != _value) { _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { _drag = true; SetFromX(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (_drag) { _drag = false; Committed?.Invoke(this, e); } base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        float cy = Height / 2f, x0 = 10, x1 = Width - 10;
        float t = (float)(_value - Min) / Math.Max(1, Max - Min), xv = x0 + t * (x1 - x0);
        using (var p = Theme.Round(new RectangleF(x0, cy - 3, x1 - x0, 6), 3)) using (var b = new SolidBrush(Theme.Border)) g.FillPath(b, p);
        using (var p = Theme.Round(new RectangleF(x0, cy - 3, Math.Max(6, xv - x0), 6), 3)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
        float r = Hover || _drag ? 9 : 8;
        using var k = new SolidBrush(Color.White);
        g.FillEllipse(k, xv - r, cy - r, r * 2, r * 2);
    }
}

class ThinProgress : Smooth
{
    double _v;
    public double Value { get => _v; set { _v = value; Invalidate(); } }
    public ThinProgress() { Height = 4; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        using (var b = new SolidBrush(Theme.Border)) g.FillRectangle(b, 0, 0, Width, Height);
        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, 0, (float)(Width * Math.Clamp(_v, 0, 1)), Height);
    }
}

// une vignette de la library
class VideoTile : Smooth
{
    public LibraryItem Item { get; }
    public bool Selected { get => _sel; set { _sel = value; Invalidate(); } }
    bool _sel;
    Image? _thumb;
    public Image? Thumb { get => _thumb; set { _thumb?.Dispose(); _thumb = value; Invalidate(); } }

    public VideoTile(LibraryItem item)
    {
        Item = item;
        Size = new Size(204, 128);
        Margin = new Padding(0, 0, 12, 12);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var outer = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var p = Theme.Round(outer, 12))
        using (var b = new SolidBrush(Hover ? Theme.PanelHover : Theme.Bg)) g.FillPath(b, p);

        var img = new RectangleF(5, 5, Width - 10, 88);
        using (var clip = Theme.Round(img, 9))
        {
            var st = g.Save();
            g.SetClip(clip);
            if (_thumb != null)
            {
                // crop au centre pour pas que l'image soit etiree
                float s = Math.Max(img.Width / _thumb.Width, img.Height / _thumb.Height);
                float sw = img.Width / s, sh = img.Height / s;
                g.DrawImage(_thumb, img, new RectangleF((_thumb.Width - sw) / 2, (_thumb.Height - sh) / 2, sw, sh), GraphicsUnit.Pixel);
            }
            else
            {
                using var b = new LinearGradientBrush(img, Color.FromArgb(40, 30, 70), Color.FromArgb(20, 22, 30), 30f);
                g.FillRectangle(b, img);
                TextRenderer.DrawText(g, "▶", Theme.H2, Rectangle.Round(img), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.Restore(st);
        }
        var txt = new Rectangle(8, 97, Width - 16, 24);
        TextRenderer.DrawText(g, Item.Name, Theme.Body, txt, _sel ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (_sel)
        {
            using var pen = new Pen(Theme.Accent, 2f);
            using var p = Theme.Round(new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), 12);
            g.DrawPath(pen, p);
            var badge = new RectangleF(img.Right - 30, img.Y + 7, 22, 22);
            using var bb = new SolidBrush(Theme.Accent);
            g.FillEllipse(bb, badge);
            TextRenderer.DrawText(g, "✓", Theme.Small, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) Thumb = null; base.Dispose(disposing); }
}

// status (rond de couleur + texte)
class StatusPill : Smooth
{
    public Color Dot { get; set; } = Theme.Muted;
    public StatusPill() { Height = 30; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Hq(g);
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f))
        using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, p);
        using (var b = new SolidBrush(Dot)) g.FillEllipse(b, 12, Height / 2f - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Theme.Small, new Rectangle(26, 0, Width - 34, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Theme.Text; base.OnRenderItemText(e); }
    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color MenuItemSelected => Theme.PanelHover;
        public override Color MenuItemBorder => Theme.PanelHover;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
    }
}
