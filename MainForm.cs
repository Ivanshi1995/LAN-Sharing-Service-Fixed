using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LanFileShare.UI;

public partial class MainForm : Form
{
    private WebView2 _webView = null!;
    private SplashPanel _splash = null!;
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private readonly string _serverUrl;
    private readonly Icon _appIcon;
    private readonly FileShareService? _service;
    private bool _minimizeToTray;
    private bool _isExiting;
    private int _navigationRetryCount;
    private string? _activeCacheDir;
    public string? ActiveCacheDir => _activeCacheDir;
    private bool _cacheNotifyShown;
    private System.Windows.Forms.Timer? _cacheCheckTimer;
    private System.Windows.Forms.Timer? _notificationRestoreTimer;
    private const int MaxNavigationRetries = 12;
    private const int ServerPollIntervalMs = 100;
    private const int NavigationRetryDelayMs = 200;
    private const int CacheCheckIntervalMs = 30 * 60 * 1000;
    private const double UiDesignWidth = 896;
    private double _uiBaseClientHeight;

    public MainForm(string serverUrl, bool minimizeToTray, Icon appIcon, FileShareSettings settings, FileShareService? service = null)
    {
        _serverUrl = serverUrl;
        _minimizeToTray = minimizeToTray;
        _appIcon = appIcon;
        _service = service;

        this.Text = $"LanFileShare 服务端 {AppVersion.Version}";
        this.MinimumSize = new Size(917, 642);
        this.Icon = _appIcon;
        this.ShowInTaskbar = true;

        // 恢复窗口大小（位置始终居中）
        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            this.Size = new Size(settings.WindowWidth, settings.WindowHeight);
        }
        else
        {
            this.Size = new Size(917, 642);
        }
        this.StartPosition = FormStartPosition.CenterScreen;

        InitializeTray();
        this.FormClosing += OnFormClosing;
        this.Resize += OnResize;

        HandleCreated += OnHandleCreated;
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        this.HandleCreated -= OnHandleCreated;
        InitializeWebView();
    }

    private void InitializeWebView()
    {
        InitializeLoadingOverlay();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        this.Controls.Add(_webView);
        _splash.BringToFront();

        _webView.CoreWebView2InitializationCompleted += (s, ev) =>
        {
            if (ev.IsSuccess && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
                {
                    args.Handled = true;
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = args.Uri,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                };
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            }
            else if (!ev.IsSuccess)
            {
                _splash.Progress = Math.Max(_splash.Progress, 20);
            }
        };
    }

    private void InitializeLoadingOverlay()
    {
        _splash = new SplashPanel { Progress = 5 };
        Controls.Add(_splash);
    }

    private async void HideLoadingOverlay()
    {
        if (_splash == null || _splash.IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(HideLoadingOverlay);
            return;
        }
        _splash.Progress = 100;
        await Task.Delay(280);
        _splash.Visible = false;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess && args.HttpStatusCode is >= 200 and < 400)
        {
            HideLoadingOverlay();
            BeginInvoke(UpdateWebViewZoom);
            return;
        }

        if (_navigationRetryCount >= MaxNavigationRetries) return;

        _navigationRetryCount++;
        await Task.Delay(NavigationRetryDelayMs);
        if (_webView?.CoreWebView2 != null && !_isExiting)
            _webView.Source = new Uri(_serverUrl);
    }

    private static async Task WaitForServerReadyAsync(string serverUrl)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var i = 0; i < MaxNavigationRetries; i++)
        {
            try
            {
                using var response = await client.GetAsync(serverUrl);
                if (response.IsSuccessStatusCode) return;
            }
            catch { /* server may still be starting */ }

            await Task.Delay(ServerPollIntervalMs);
        }
    }

    private void UpdateWebViewZoom()
    {
        if (_webView?.CoreWebView2 == null) return;

        var w = ClientSize.Width;
        var h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        if (_uiBaseClientHeight <= 0)
            _uiBaseClientHeight = h;

        var scaleW = w / UiDesignWidth;
        var scaleH = h / _uiBaseClientHeight;
        var scale = Math.Max(1.0, Math.Min(scaleW, scaleH));

        if (Math.Abs(_webView.ZoomFactor - scale) > 0.001)
            _webView.ZoomFactor = scale;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 确保 WebView2 控件已创建
        if (_webView == null)
        {
            InitializeWebView();
        }

        // 在初始化前先清理可能的锁文件，防止上次进程残留导致锁定
        var baseCache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview_cache");
        WebViewCacheHelper.DeleteOrphanCacheDirectories();
        CleanWebViewLockFiles(baseCache);
        _splash.Progress = 15;

        Exception? lastException = null;
        string? lastCacheDir = null;

        async Task<bool> TryInitAsync(string cacheDir, int progressAfter)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                _splash.Progress = progressAfter;
                var envTask = CoreWebView2Environment.CreateAsync(null, cacheDir);
                var serverTask = WaitForServerReadyAsync(_serverUrl);
                var env = await envTask;
                _splash.Progress = progressAfter + 25;
                await Task.WhenAll(_webView!.EnsureCoreWebView2Async(env), serverTask);
                _splash.Progress = progressAfter + 45;
                _activeCacheDir = cacheDir;
                _webView.Source = new Uri(_serverUrl);
                _splash.Progress = 92;
                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;
                lastCacheDir = cacheDir;
                System.Diagnostics.Debug.WriteLine($"WebView2 初始化失败 [{cacheDir}]: {ex.GetType().Name} - {ex.Message}");
                if (ex.InnerException != null)
                    System.Diagnostics.Debug.WriteLine($"  Inner: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                return false;
            }
        }

        if (await TryInitAsync(baseCache, 20))
        {
            StartCacheMonitor();
            return;
        }

        CleanWebViewLockFiles(baseCache);
        await Task.Delay(150);
        if (await TryInitAsync(baseCache, 30))
        {
            StartCacheMonitor();
            return;
        }

        var newCache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"webview_cache_{DateTime.Now:yyyyMMddHHmmss}");
        if (await TryInitAsync(newCache, 35))
        {
            StartCacheMonitor();
            return;
        }

        _splash.Visible = false;
        // 显示详细错误信息
        var errorMsg = $"WebView2 初始化失败\n\n" +
                      $"错误类型: {lastException?.GetType().Name}\n" +
                      $"错误信息: {lastException?.Message}\n" +
                      $"缓存目录: {lastCacheDir}\n\n" +
                      $"请确保已安装 Microsoft Edge WebView2 Runtime。\n" +
                      $"如问题持续，请手动删除程序目录下的 webview_cache 文件夹后重试。";
        
        MessageBox.Show(errorMsg, "初始化错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void CleanWebViewLockFiles(string cacheDir)
    {
        try
        {
            if (!Directory.Exists(cacheDir)) return;

            // 删除所有 .lock 文件
            foreach (var f in Directory.GetFiles(cacheDir, "*.lock", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }

            // 删除 SingletonLock 文件（这是最关键的锁文件）
            var singleton = Path.Combine(cacheDir, "EBWebView", "SingletonLock");
            if (File.Exists(singleton)) File.Delete(singleton);

            // 删除其他可能的锁文件
            var singletonSocket = Path.Combine(cacheDir, "EBWebView", "SingletonSocket");
            if (File.Exists(singletonSocket)) File.Delete(singletonSocket);

            var singletonCookie = Path.Combine(cacheDir, "EBWebView", "SingletonCookie");
            if (File.Exists(singletonCookie)) File.Delete(singletonCookie);
        }
        catch { }
    }

    private void StartCacheMonitor()
    {
        _cacheCheckTimer?.Stop();
        _cacheCheckTimer?.Dispose();

        _cacheCheckTimer = new System.Windows.Forms.Timer { Interval = CacheCheckIntervalMs };
        _cacheCheckTimer.Tick += (_, _) => CheckCacheThreshold();
        _cacheCheckTimer.Start();

        _ = Task.Run(async () =>
        {
            await Task.Delay(15000);
            if (!_isExiting)
                BeginInvoke(CheckCacheThreshold);
        });
    }

    public void OnSettingsChanged() => CheckCacheThreshold();

    public object GetWebViewCacheStatus()
    {
        var limitGb = _service?.Settings.WebViewCacheLimitGB ?? 1.0;
        var sizeBytes = WebViewCacheHelper.GetTotalCacheSizeBytes();
        var limitBytes = WebViewCacheHelper.GbToBytes(limitGb);
        return new
        {
            sizeBytes,
            sizeDisplay = WebViewCacheHelper.FormatSize(sizeBytes),
            limitGB = limitGb,
            exceeds = limitGb > 0 && sizeBytes >= limitBytes
        };
    }

    public async Task<WebViewCacheCleanResult> CleanWebViewCacheAsync()
    {
        var limitGb = _service?.Settings.WebViewCacheLimitGB ?? 1.0;
        var sizeBefore = WebViewCacheHelper.GetTotalCacheSizeBytes();
        var limitBytes = WebViewCacheHelper.GbToBytes(limitGb);

        if (limitGb <= 0)
        {
            return new WebViewCacheCleanResult
            {
                Success = false,
                Message = "请先在设置中配置缓存上限"
            };
        }

        if (sizeBefore < limitBytes)
        {
            return new WebViewCacheCleanResult
            {
                Success = false,
                SizeBefore = sizeBefore,
                SizeBeforeDisplay = WebViewCacheHelper.FormatSize(sizeBefore),
                Message = $"当前缓存 {WebViewCacheHelper.FormatSize(sizeBefore)}，未超过上限 {limitGb:G} GB"
            };
        }

        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.CacheStorage);
            }

            WebViewCacheHelper.DeleteInactiveCacheDirectories(_activeCacheDir);
            WebViewCacheHelper.CleanDirectoryContents(_activeCacheDir);

            var sizeAfter = WebViewCacheHelper.GetTotalCacheSizeBytes();
            _cacheNotifyShown = false;

            if (_webView?.CoreWebView2 != null && !_isExiting)
                _webView.Source = new Uri(_serverUrl);

            _service?.AddLog("INFO", "服务端",
                $"WebView 缓存已清理 {WebViewCacheHelper.FormatSize(sizeBefore)} → {WebViewCacheHelper.FormatSize(sizeAfter)}");

            return new WebViewCacheCleanResult
            {
                Success = true,
                SizeBefore = sizeBefore,
                SizeAfter = sizeAfter,
                SizeBeforeDisplay = WebViewCacheHelper.FormatSize(sizeBefore),
                SizeAfterDisplay = WebViewCacheHelper.FormatSize(sizeAfter),
                Message = $"清理完成：{WebViewCacheHelper.FormatSize(sizeBefore)} → {WebViewCacheHelper.FormatSize(sizeAfter)}"
            };
        }
        catch (Exception ex)
        {
            _service?.AddLog("ERROR", "服务端", $"WebView 缓存清理失败: {ex.Message}");
            return new WebViewCacheCleanResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private void CheckCacheThreshold()
    {
        if (_service == null)
            return;

        var limitGb = _service.Settings.WebViewCacheLimitGB;
        if (limitGb <= 0)
        {
            _cacheNotifyShown = false;
            return;
        }

        var sizeBytes = WebViewCacheHelper.GetTotalCacheSizeBytes();
        var limitBytes = WebViewCacheHelper.GbToBytes(limitGb);

        if (sizeBytes >= limitBytes)
        {
            if (_cacheNotifyShown)
                return;

            _cacheNotifyShown = true;
            var sizeDisplay = WebViewCacheHelper.FormatSize(sizeBytes);
            _service.AddLog("WARN", "服务端", $"WebView 缓存超限 {sizeDisplay} / {limitGb:G} GB");
            ShowSystemNotification(
                "LanFileShare 缓存提醒",
                $"WebView 缓存已达 {sizeDisplay}，超过上限 {limitGb:G} GB。请点击「清理缓存」按钮进行清理。");
        }
        else
        {
            _cacheNotifyShown = false;
        }
    }

    private void ShowSystemNotification(string title, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowSystemNotification(title, message));
            return;
        }

        try
        {
            var keepVisible = _minimizeToTray;
            if (!_trayIcon.Visible)
                _trayIcon.Visible = true;

            _trayIcon.ShowBalloonTip(8000, title, message, ToolTipIcon.Warning);

            if (!keepVisible)
            {
                _notificationRestoreTimer?.Stop();
                _notificationRestoreTimer?.Dispose();
                _notificationRestoreTimer = new System.Windows.Forms.Timer { Interval = 9000 };
                _notificationRestoreTimer.Tick += (_, _) =>
                {
                    _notificationRestoreTimer?.Stop();
                    if (!_minimizeToTray && !_isExiting)
                        _trayIcon.Visible = false;
                };
                _notificationRestoreTimer.Start();
            }
        }
        catch { /* notification is best-effort */ }
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示主窗口", null, OnTrayShow);
        var exitItem = new ToolStripMenuItem("退出", null, OnTrayExit);
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Text = $"LanFileShare {AppVersion.Version}",
            Icon = _appIcon,
            ContextMenuStrip = _trayMenu,
            Visible = _minimizeToTray
        };
        _trayIcon.DoubleClick += OnTrayShow;
    }

    private void OnTrayShow(object? sender, EventArgs e)
    {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.Activate();
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        _isExiting = true;
        _trayIcon.Visible = false;
        this.Close();
    }

    public void RequestExitForUpdate()
    {
        _isExiting = true;
        try { _trayIcon.Visible = false; } catch { }

        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Stop();
                _webView.Dispose();
                _webView = null!;
            }
        }
        catch { }

        Environment.Exit(0);
    }

    // 启动时自动最小化到托盘
    public void AutoMinimizeToTrayIfEnabled()
    {
        if (!_minimizeToTray || _isExiting) return;
        BeginInvoke(() =>
        {
            if (_minimizeToTray && !_isExiting)
            {
                this.WindowState = FormWindowState.Minimized;
            }
        });
    }

    public void ApplyMinimizeToTray(bool minimizeToTray)
    {
        void Apply()
        {
            _minimizeToTray = minimizeToTray;
            if (!minimizeToTray)
            {
                _trayIcon.Visible = false;
                ShowInTaskbar = true;
                if (!Visible)
                {
                    Show();
                    WindowState = FormWindowState.Normal;
                    Activate();
                }
            }
        }

        if (InvokeRequired)
            BeginInvoke(Apply);
        else
            Apply();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting && _minimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            this.Hide();
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(2000, "LanFileShare",
                "程序已最小化到系统托盘，双击图标可恢复窗口。", ToolTipIcon.Info);
            return;
        }

        // 退出时强制关闭 WebView2，防止子进程残留锁定缓存
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Stop();
                _webView.Dispose();
                _webView = null!;
            }
        }
        catch { }

        // 等待一小段时间让 WebView2 子进程完全退出
        System.Threading.Thread.Sleep(500);

        // 清理锁文件，为下次启动做准备
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webview_cache");
        CleanWebViewLockFiles(cacheDir);
    }

    private bool _trayMinimizedNotified;

    private void OnResize(object? sender, EventArgs e)
    {
        UpdateWebViewZoom();
        if (_minimizeToTray && this.WindowState == FormWindowState.Minimized)
        {
            this.Hide();
            _trayIcon.Visible = true;
            if (!_trayMinimizedNotified)
            {
                _trayMinimizedNotified = true;
                _trayIcon.ShowBalloonTip(3000, "LanFileShare",
                    "程序已最小化到系统托盘，双击图标可恢复窗口。", ToolTipIcon.Info);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.Stop();
                    }
                    _webView.Dispose();
                    _webView = null!;
                }
            }
            catch { }
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _splash?.Dispose();
            _cacheCheckTimer?.Stop();
            _cacheCheckTimer?.Dispose();
            _notificationRestoreTimer?.Stop();
            _notificationRestoreTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
