using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SqlMigrationTool.Forms;

public class frmSplash : Form
{
    private int  _tick = 0;
    private const int TotalTicks = 240; // 25 ms × 240 = 6 000 ms fill time

    private System.Windows.Forms.Timer? _fadeTimer;
    private System.Windows.Forms.Timer? _progTimer;

    private Rectangle _skipRect;
    private bool      _skipHover;

    public frmSplash()
    {
        FormBorderStyle = FormBorderStyle.None;
        Size            = new Size(600, 355);
        StartPosition   = FormStartPosition.CenterScreen;
        ShowInTaskbar   = false;
        BackColor       = Color.FromArgb(18, 19, 30);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.DoubleBuffer,
            true);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Opacity = 0;

        // Phase 1 — fade in (~340 ms)
        _fadeTimer = new System.Windows.Forms.Timer { Interval = 18 };
        _fadeTimer.Tick += (_, _) =>
        {
            Opacity = Math.Min(1.0, Opacity + 0.055);
            if (Opacity >= 1.0)
            {
                _fadeTimer!.Stop();

                // Phase 2 — progress bar fills then closes
                _progTimer = new System.Windows.Forms.Timer { Interval = 25 };
                _progTimer.Tick += (_, _) =>
                {
                    _tick++;
                    Invalidate();
                    if (_tick >= TotalTicks) { _progTimer!.Stop(); Close(); }
                };
                _progTimer.Start();
            }
        };
        _fadeTimer.Start();
    }

    // ── Skip ─────────────────────────────────────────────────────────────────

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_skipRect.Contains(e.Location)) Close();
        base.OnMouseClick(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = _skipRect.Contains(e.Location);
        if (hover != _skipHover)
        {
            _skipHover = hover;
            Cursor     = hover ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int W = ClientSize.Width;
        int H = ClientSize.Height;

        // Background gradient
        using (var bg = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(28, 30, 50), Color.FromArgb(10, 11, 20),
            LinearGradientMode.Vertical))
            g.FillRectangle(bg, ClientRectangle);

        // Top accent stripe  VS-purple → MS-blue
        using (var stripe = new LinearGradientBrush(
            new Rectangle(0, 0, W, 4),
            Color.FromArgb(104, 33, 122), Color.FromArgb(0, 120, 212),
            LinearGradientMode.Horizontal))
            g.FillRectangle(stripe, 0, 0, W, 4);

        // Application logo (image1.png)
        const int logoSz = 82;
        int logoX = (W - logoSz) / 2;
        g.DrawImage(AppImage.Logo, logoX, 10, logoSz, logoSz);

        // Title
        using (var f = new Font("Segoe UI", 21, FontStyle.Bold))
        {
            const string t = "SQL Migration Tool";
            var sz = g.MeasureString(t, f);
            g.DrawString(t, f, Brushes.White, (W - sz.Width) / 2f, 98f);
        }

        // Subtitle
        using (var f  = new Font("Segoe UI", 10.5f))
        using (var br = new SolidBrush(Color.FromArgb(175, 180, 215)))
        {
            const string t = "Database Backup & Restore Utility";
            var sz = g.MeasureString(t, f);
            g.DrawString(t, f, br, (W - sz.Width) / 2f, 132f);
        }

        // Divider
        using (var pen = new Pen(Color.FromArgb(52, 58, 85), 1))
            g.DrawLine(pen, 44, 162, W - 44, 162);

        // Description
        string[] desc =
        {
            "Migrate SQL Server databases across versions using portable SQL scripts.",
            "Exports complete schema, data, relationships and indexes as text files —",
            "enabling seamless backup and restore between any SQL Server versions."
        };
        using (var f  = new Font("Segoe UI", 8.5f))
        using (var br = new SolidBrush(Color.FromArgb(145, 150, 180)))
        {
            float dy = 172f;
            foreach (var line in desc)
            {
                var sz = g.MeasureString(line, f);
                g.DrawString(line, f, br, (W - sz.Width) / 2f, dy);
                dy += 18f;
            }
        }

        // Author
        using (var f  = new Font("Segoe UI", 8.5f))
        using (var br = new SolidBrush(Color.FromArgb(165, 170, 200)))
        {
            const string t = "Created by   Praveen Kuttathara Prasannan";
            var sz = g.MeasureString(t, f);
            g.DrawString(t, f, br, (W - sz.Width) / 2f, 240f);
        }

        // Claude credit
        using (var f  = new Font("Segoe UI", 8.5f, FontStyle.Italic))
        using (var br = new SolidBrush(Color.FromArgb(110, 170, 255)))
        {
            const string t = "✦  Completely developed by Claude AI  (Anthropic)";
            var sz = g.MeasureString(t, f);
            g.DrawString(t, f, br, (W - sz.Width) / 2f, 259f);
        }

        // Version (bottom-right)
        using (var f  = new Font("Segoe UI", 7.5f))
        using (var br = new SolidBrush(Color.FromArgb(72, 76, 105)))
        {
            const string t = "Version 1.0.0";
            var sz = g.MeasureString(t, f);
            g.DrawString(t, f, br, W - sz.Width - 14, H - 22f);
        }

        // Skip link (bottom-left, above progress bar)
        using (var f  = new Font("Segoe UI", 8.5f,
                   _skipHover ? FontStyle.Underline : FontStyle.Regular))
        using (var br = new SolidBrush(
                   _skipHover ? Color.FromArgb(170, 175, 210) : Color.FromArgb(80, 85, 118)))
        {
            const string t = "Skip  ›";
            var sz = g.MeasureString(t, f);
            float sx = 14f;
            float sy = H - 22f;
            g.DrawString(t, f, br, sx, sy);
            _skipRect = new Rectangle((int)sx - 1, (int)sy - 1,
                                      (int)sz.Width + 2, (int)sz.Height + 2);
        }

        // Progress track
        g.FillRectangle(new SolidBrush(Color.FromArgb(38, 42, 62)), 0, H - 5, W, 5);

        // Progress fill  purple → blue
        if (_tick > 0)
        {
            int fillW = Math.Max(2, (int)((double)_tick / TotalTicks * W));
            using var fill = new LinearGradientBrush(
                new Rectangle(0, H - 5, fillW, 5),
                Color.FromArgb(104, 33, 122), Color.FromArgb(0, 120, 212),
                LinearGradientMode.Horizontal);
            g.FillRectangle(fill, 0, H - 5, fillW, 5);
        }

        // Outer border
        using (var pen = new Pen(Color.FromArgb(55, 60, 88), 1))
            g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _fadeTimer?.Stop();  _fadeTimer?.Dispose();
        _progTimer?.Stop();  _progTimer?.Dispose();
        base.OnFormClosed(e);
    }
}
