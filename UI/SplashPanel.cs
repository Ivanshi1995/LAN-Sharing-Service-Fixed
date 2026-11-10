using System.Drawing.Drawing2D;

namespace LanFileShare.UI;

public class SplashPanel : Panel
{
    private int _progress;
    private int _target;
    private System.Windows.Forms.Timer? _timer;

    public SplashPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(240, 242, 245);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) =>
        {
            if (_progress < _target)
            {
                _progress = Math.Min(_target, _progress + Math.Max(1, (_target - _progress) / 4));
                Invalidate();
            }
            else if (_target < 100 && _progress < 90)
            {
                _progress++;
                Invalidate();
            }
        };
        _timer.Start();
    }

    public int Progress
    {
        get => _progress;
        set
        {
            _target = Math.Clamp(value, 0, 100);
            if (_progress < _target) Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = ClientRectangle;
        using (var bg = new SolidBrush(Color.FromArgb(245, 247, 250)))
            g.FillRectangle(bg, r);

        const string title = "正在启动LanFileShare";
        var barW = Math.Min(480f, r.Width - 80f);
        var barH = 12f;
        var barX = (r.Width - barW) / 2f;
        var barY = r.Height / 2f + 8f;

        using var titleFont = new Font("Segoe UI", 10f);
        using var titleBrush = new SolidBrush(Color.FromArgb(120, 128, 138));
        var titleSize = g.MeasureString(title, titleFont);
        g.DrawString(title, titleFont, titleBrush, (r.Width - titleSize.Width) / 2f, barY - titleSize.Height - 28f);

        var track = new RectangleF(barX, barY, barW, barH);
        FillRound(g, track, barH / 2f, Color.FromArgb(225, 228, 233));

        var fillW = barW * (_progress / 100f);
        if (fillW > 0)
            FillRound(g, new RectangleF(barX, barY, fillW, barH), barH / 2f, Color.FromArgb(59, 130, 246));

        var pct = _progress + "%";
        using var pctFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var pctBrush = new SolidBrush(Color.FromArgb(90, 100, 115));
        var pctSize = g.MeasureString(pct, pctFont);
        var pctX = Math.Clamp(barX + fillW - pctSize.Width / 2f - 8f, barX, barX + barW - pctSize.Width - 8f);
        g.DrawString(pct, pctFont, pctBrush, pctX, barY - pctSize.Height - 14f);
    }

    private static void FillRound(Graphics g, RectangleF rect, float radius, Color color)
    {
        using var path = RoundRect(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            _timer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
