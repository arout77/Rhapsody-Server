using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using RhapsodyServer.Core;

namespace RhapsodyServer.Tray;

/// <summary>
/// The tray menu's look: the dashboard's palette (deep violet and periwinkle, with green and red for start and stop),
/// in a light or dark version that follows the Windows "app mode" setting. Colour is only ever an addition: every status
/// icon also has its own shape, and every item keeps its text.
/// </summary>
sealed class TrayTheme
{
    public bool Dark { get; }
    public Color Background, Border, Separator, Hover, HoverBorder, Text, TextDisabled, Accent, Go, Stop, Warn, Muted;

    TrayTheme(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Background = Color.FromArgb(0x1b, 0x10, 0x38);
            Border = Color.FromArgb(0x3a, 0x2a, 0x6a);
            Separator = Color.FromArgb(0x33, 0x25, 0x5e);
            Hover = Color.FromArgb(0x2e, 0x1d, 0x5e);
            HoverBorder = Color.FromArgb(0x4d, 0x2a, 0xaf);
            Text = Color.FromArgb(0xee, 0xee, 0xf2);
            TextDisabled = Color.FromArgb(0x84, 0x7d, 0x9a);
            Accent = Color.FromArgb(0x96, 0xb4, 0xff);
            Go = Color.FromArgb(0x52, 0xdb, 0x9c);
            Stop = Color.FromArgb(0xff, 0x95, 0xb3);
            Warn = Color.FromArgb(0xf1, 0xbf, 0x4e);
            Muted = Color.FromArgb(0xa8, 0xa2, 0xbd);
        }
        else
        {
            Background = Color.White;
            Border = Color.FromArgb(0xc9, 0xc6, 0xd6);
            Separator = Color.FromArgb(0xdb, 0xdb, 0xdb);
            Hover = Color.FromArgb(0xec, 0xe8, 0xfd);
            HoverBorder = Color.FromArgb(0xc7, 0xb8, 0xf7);
            Text = Color.FromArgb(0x14, 0x0a, 0x2e);
            TextDisabled = Color.FromArgb(0x9b, 0x96, 0xab);
            Accent = Color.FromArgb(0x5e, 0x2d, 0xe9);
            Go = Color.FromArgb(0x00, 0x86, 0x4e);
            Stop = Color.FromArgb(0xe5, 0x00, 0x3a);
            Warn = Color.FromArgb(0xd4, 0x8e, 0x00);
            Muted = Color.FromArgb(0x6d, 0x67, 0x7d);
        }
    }

    /// <summary>The current Windows app mode. Light if it can't be read.</summary>
    public static TrayTheme Current()
    {
        var light = true;
        try { light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int v || v != 0; }
        catch { /* keep light */ }
        return new TrayTheme(!light);
    }
}

sealed class TrayColors : ProfessionalColorTable
{
    readonly TrayTheme _t;
    public TrayColors(TrayTheme t) { _t = t; }

    public override Color ToolStripDropDownBackground => _t.Background;
    public override Color ImageMarginGradientBegin => _t.Background;
    public override Color ImageMarginGradientMiddle => _t.Background;
    public override Color ImageMarginGradientEnd => _t.Background;
    public override Color MenuBorder => _t.Border;
    public override Color MenuItemBorder => _t.HoverBorder;
    public override Color MenuItemSelected => _t.Hover;
    public override Color MenuItemSelectedGradientBegin => _t.Hover;
    public override Color MenuItemSelectedGradientEnd => _t.Hover;
    public override Color MenuItemPressedGradientBegin => _t.Hover;
    public override Color MenuItemPressedGradientMiddle => _t.Hover;
    public override Color MenuItemPressedGradientEnd => _t.Hover;
    public override Color SeparatorDark => _t.Separator;
    public override Color SeparatorLight => _t.Separator;
}

sealed class TrayRenderer : ToolStripProfessionalRenderer
{
    readonly TrayTheme _t;
    public TrayRenderer(TrayTheme t) : base(new TrayColors(t)) { _t = t; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // An item may carry its own colour in Tag (Open dashboard); otherwise the theme's. Disabled items are always muted.
        var themed = e.Item.Tag is Color own ? own : _t.Text;
        e.TextColor = e.Item.Enabled ? themed : _t.TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item.Enabled ? _t.Muted : _t.TextDisabled;
        base.OnRenderArrow(e);
    }
}

/// <summary>Small drawn icons for the menu, made once per theme and size. No image files are shipped.</summary>
sealed class TrayIcons : IDisposable
{
    readonly TrayTheme _t;
    readonly int _px;
    readonly Dictionary<string, Bitmap> _cache = new();

    public TrayIcons(TrayTheme t, int dpi) { _t = t; _px = Math.Max(16, (int)Math.Round(16 * dpi / 96.0)); }

    public Image Status(ServiceState s) => s switch
    {
        ServiceState.Running => Get("run", g => { Fill(g, _t.Go, 3.5f, 3.5f, 9f, 9f, ellipse: true); }),
        ServiceState.Starting => Get("start", g => { Ring(g, _t.Warn, 3.5f, 3.5f, 9f, 9f); Pie(g, _t.Warn, 3.5f, 3.5f, 9f, 9f); }),
        ServiceState.Crashed => Get("crash", g => { Cross(g, _t.Stop, 4f, 4f, 12f, 12f); }),
        _ => Get("off", g => { Ring(g, _t.Muted, 3.5f, 3.5f, 9f, 9f); }),
    };

    public Image Play => Get("play", g => Poly(g, _t.Go, new PointF(4.5f, 3f), new PointF(13f, 8f), new PointF(4.5f, 13f)));
    public Image StopSq => Get("stop", g => Rounded(g, _t.Stop, 3.5f, 3.5f, 9f, 9f, 1.6f));
    public Image Dashboard => Get("dash", g =>
    {
        Rounded(g, _t.Accent, 2.5f, 2.5f, 4.8f, 4.8f, 1.2f); Rounded(g, _t.Accent, 8.7f, 2.5f, 4.8f, 4.8f, 1.2f);
        Rounded(g, _t.Accent, 2.5f, 8.7f, 4.8f, 4.8f, 1.2f); Rounded(g, _t.Accent, 8.7f, 8.7f, 4.8f, 4.8f, 1.2f);
    });
    public Image Globe => Get("globe", g =>
    {
        using var p = MakePen(_t.Accent, 1.3f);
        g.DrawEllipse(p, S(2.5f), S(2.5f), S(11f), S(11f));
        g.DrawEllipse(p, S(5.6f), S(2.5f), S(4.8f), S(11f));
        g.DrawLine(p, S(2.5f), S(8f), S(13.5f), S(8f));
    });
    public Image Folder => Get("folder", g =>
    {
        using var p = MakePen(_t.Accent, 1.3f);
        g.DrawLines(p, new[] { new PointF(S(2.5f), S(12.5f)), new PointF(S(2.5f), S(4f)), new PointF(S(6.5f), S(4f)), new PointF(S(8f), S(5.5f)), new PointF(S(13.5f), S(5.5f)), new PointF(S(13.5f), S(12.5f)), new PointF(S(2.5f), S(12.5f)) });
    });
    public Image Edit => Get("edit", g =>
    {
        using var p = MakePen(_t.Accent, 1.4f);
        g.DrawLine(p, S(3f), S(13f), S(4f), S(10f)); g.DrawLine(p, S(4f), S(10f), S(10.5f), S(3.5f));
        g.DrawLine(p, S(10.5f), S(3.5f), S(12.5f), S(5.5f)); g.DrawLine(p, S(12.5f), S(5.5f), S(6f), S(12f)); g.DrawLine(p, S(6f), S(12f), S(3f), S(13f));
    });
    public Image Help => Get("help", g =>
    {
        using var p = MakePen(_t.Accent, 1.3f);
        g.DrawEllipse(p, S(2.5f), S(2.5f), S(11f), S(11f));
        g.DrawArc(p, S(5.6f), S(5f), S(4.8f), S(3.8f), 200, 250);
        g.DrawLine(p, S(8f), S(8.4f), S(8f), S(9.6f));
        using var b = new SolidBrush(_t.Accent); g.FillEllipse(b, S(7.4f), S(10.6f), S(1.3f), S(1.3f));
    });
    public Image Power => Get("power", g =>
    {
        using var p = MakePen(_t.Stop, 1.5f);
        g.DrawArc(p, S(3.2f), S(3.6f), S(9.6f), S(9.6f), -55, 290);
        g.DrawLine(p, S(8f), S(2.2f), S(8f), S(7.4f));
    });

    // -------------------------------------------------------------- drawing
    float S(float v) => v * _px / 16f;
    Pen MakePen(Color c, float w) => new Pen(c, w * _px / 16f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    Image Get(string key, Action<Graphics> draw)
    {
        if (_cache.TryGetValue(key, out var have)) return have;
        var bmp = new Bitmap(_px, _px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            draw(g);
        }
        _cache[key] = bmp;
        return bmp;
    }

    void Fill(Graphics g, Color c, float x, float y, float w, float h, bool ellipse)
    {
        using var b = new SolidBrush(c);
        if (ellipse) g.FillEllipse(b, S(x), S(y), S(w), S(h)); else g.FillRectangle(b, S(x), S(y), S(w), S(h));
    }
    void Ring(Graphics g, Color c, float x, float y, float w, float h) { using var p = MakePen(c, 1.4f); g.DrawEllipse(p, S(x), S(y), S(w), S(h)); }
    void Pie(Graphics g, Color c, float x, float y, float w, float h) { using var b = new SolidBrush(c); g.FillPie(b, S(x), S(y), S(w), S(h), -90, 180); }
    void Cross(Graphics g, Color c, float x1, float y1, float x2, float y2) { using var p = MakePen(c, 1.7f); g.DrawLine(p, S(x1), S(y1), S(x2), S(y2)); g.DrawLine(p, S(x1), S(y2), S(x2), S(y1)); }
    void Poly(Graphics g, Color c, params PointF[] pts) { using var b = new SolidBrush(c); g.FillPolygon(b, pts.Select(p => new PointF(S(p.X), S(p.Y))).ToArray()); }
    void Rounded(Graphics g, Color c, float x, float y, float w, float h, float r)
    {
        using var path = new GraphicsPath();
        float X = S(x), Y = S(y), W = S(w), H = S(h), R = S(r) * 2;
        path.AddArc(X, Y, R, R, 180, 90); path.AddArc(X + W - R, Y, R, R, 270, 90);
        path.AddArc(X + W - R, Y + H - R, R, R, 0, 90); path.AddArc(X, Y + H - R, R, R, 90, 90);
        path.CloseFigure();
        using var b = new SolidBrush(c); g.FillPath(b, path);
    }

    public void Dispose() { foreach (var b in _cache.Values) b.Dispose(); _cache.Clear(); }
}
