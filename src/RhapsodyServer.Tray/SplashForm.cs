namespace RhapsodyServer.Tray;

/// <summary>
/// The launch splash: the artwork on screen for a moment, then it fades away. Nothing waits on it: the tray icon
/// and menu work immediately. Skipped with --no-splash (the start-with-Windows shortcut passes that).
/// </summary>
sealed class SplashForm : Form
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 25 };
    readonly Bitmap _art;
    DateTime _hideAt;

    public SplashForm(TimeSpan visibleFor)
    {
        using var s = typeof(SplashForm).Assembly.GetManifestResourceStream("splash.jpg")
            ?? throw new InvalidOperationException("splash.jpg is not embedded");
        using var img = Image.FromStream(s);
        _art = new Bitmap(img);                               // copy, so the stream can close

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(3, 8, 20);
        BackgroundImage = _art;
        BackgroundImageLayout = ImageLayout.Stretch;
        DoubleBuffered = true;
        var w = (int)(720 * DeviceDpi / 96f);                 // 16:9, scaled for the screen's DPI
        ClientSize = new Size(w, w * _art.Height / _art.Width);

        _hideAt = DateTime.UtcNow + visibleFor;
        _timer.Tick += (_, _) =>
        {
            var left = (_hideAt - DateTime.UtcNow).TotalMilliseconds;
            if (left > 0) return;
            Opacity -= 0.1;                                    // ~250 ms fade
            if (Opacity <= 0.05) { _timer.Stop(); Close(); }
        };
        Shown += (_, _) => _timer.Start();
        Click += (_, _) => Close();                            // click to dismiss early
    }

    protected override bool ShowWithoutActivation => true;     // never steals focus from what you were typing

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _art.Dispose(); }
        base.Dispose(disposing);
    }
}
