using System.Drawing.Drawing2D;

namespace LanFileShare.UI;

public class StartupSplashPanel : Panel
{
    private const string TitleText = "正在启动LanFileShare";
    private int _progress;
    private int _targetProgress;
    private System.Windows.Forms.Timer? _animTimer;

    public StartupSplashPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(240, 242, 245);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        StartAnimation();
    }

    public int Progress
    {
        get => _progress;
        set
        {
            _targetProgress = Math.Clamp(value, 0, 100);
            if (_progress < _targetProgress)
                Invalidate();
        }
    }

    public void SetProgress(int value)
    {
        Progress = value;
    }

    private void StartAnimation()
    {
        _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animTimer.Tick += (_, _) =>
        {
            if (_progress < _targetProgress)
            {
                var step = Math.Max(1, (_targetProgress - _progress) / 4);
                _progress = Math.Min(_targetProgress, _progress + step);
                Invalidate();
            }
            else if (_progress < 95 && _targetProgress >= 95)
            {
                // 等待最终完成时缓慢推进
            }
            else if (_targetProgress < 100 && _progress < 90)
            {
                _progress = Math.Min(90, _progress + 1);
                Invalidate();
            }
        };
        _animTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = ClientRectangle;
        using (var bg = new LinearGradientBrush(bounds, Color.FromArgb(248, 249, 251), Color.FromArgb(235, 238, 242), 90f))
            g.FillRectangle(bg, bounds);

        var centerX = bounds.Width / 2f;
        var barWidth = Math.Min(520f, bounds.Width - 80f);
        var barHeight = 14f;
        var barLeft = centerX - barWidth / 2f;
        var barTop = bounds.Height / 2f + 10f;

        using var titleFont = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        var titleSize = g.MeasureString(TitleText, titleFont);
        var titleY = barTop - titleSize.Height - 36f;
        using var titleBrush = new SolidBrush(Color.FromArgb(130, 138, 148));
        g.DrawString(TitleText, titleFont, titleBrush, centerX - titleSize.Width / 2f, titleY);

        var trackRect = new RectangleF(barLeft, barTop, barWidth, barHeight);
        DrawTrack(g, trackRect);

        var fillWidth = barWidth * (_progress / 100f);
        if (fillWidth > 1f)
        {
            var fillRect = new RectangleF(barLeft, barTop, fillWidth, barHeight);
            DrawFill(g, fillRect, barHeight);
            DrawPercentBubble(g, fillRect.Right, barTop, barHeight);
        }
        else if (_progress > 0)
        {
            DrawPercentBubble(g, barLeft + 8f, barTop, barHeight);
        }
    }

    private static void DrawTrack(Graphics g, RectangleF rect)
    {
        var radius = rect.Height / 2f;
        using var path = CreateRoundRect(rect, radius);
        using var fill = new SolidBrush(Color.FromArgb(228, 231, 236));
        g.FillPath(fill, path);
        using var inner = new Pen(Color.FromArgb(210, 214, 220));
        g.DrawPath(inner, path);
    }

    private static void DrawFill(Graphics g, RectangleF rect, float barHeight)
    {
        var radius = barHeight / 2f;
        using var path = CreateRoundRect(rect, radius);
        using var brush = new LinearGradientBrush(rect, Color.FromArgb(96, 165, 250), Color.FromArgb(59, 130, 246), 90f);
        g.FillPath(brush, path);
        using var highlight = new LinearGradientBrush(rect, Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
        g.FillPath(highlight, path);
    }

    private void DrawPercentBubble(Graphics g, float anchorX, float barTop, float barHeight)
    {
        var text = _progress + "%";
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        var textSize = g.MeasureString(text, font);
        var padX = 10f;
        var padY = 5f;
        var bubbleW = textSize.Width + padX * 2;
        var bubbleH = textSize.Height + padY * 2;
        var bubbleX = Math.Clamp(anchorX - bubbleW / 2f, 8f, ClientSize.Width - bubbleW - 8f);
        var bubbleY = barTop - bubbleH - 12f;

        var bubbleRect = new RectangleF(bubbleX, bubbleY, bubbleW, bubbleH);
        using var shadowPath = CreateRoundRect(new RectangleF(bubbleRect.X + 1, bubbleRect.Y + 2, bubbleRect.Width, bubbleRect.Height), 6f);
        using var shadow = new SolidBrush(Color.FromArgb(35, 0, 0, 0));
        g.FillPath(shadow, shadowPath);

        using var bubblePath = CreateRoundRect(bubbleRect, 6f);
        using var bubbleFill = new SolidBrush(Color.White);
        g.FillPath(bubbleFill, bubblePath);

        var tipX = Math.Clamp(anchorX, bubbleRect.Left + 10, bubbleRect.Right - 10);
        var tipTop = bubbleRect.Bottom;
        var tipBottom = barTop - 2f;
        using var tip = new GraphicsPath();
        tip.AddPolygon(new[]
        {
            new PointF(tipX - 5, tipTop - 0.5f),
            new PointF(tipX + 5, tipTop - 0.5f),
            new PointF(tipX, tipBottom)
        });
        g.FillPath(bubbleFill, tip);

        using var textBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
        g.DrawString(text, font, textBrush, bubbleRect.X + padX, bubbleRect.Y + padY - 1f);
    }

    private static GraphicsPath CreateRoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2f;
        if (d > rect.Height) d = rect.Height;
        if (d > rect.Width) d = rect.Width;
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
            _animTimer?.Stop();
            _animTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
