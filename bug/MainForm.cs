using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BugUploader;

public class MainForm : Form
{
    private readonly string _url;
    private WebView2? _webView;

    public MainForm(string url)
    {
        _url = url;
        InitializeForm();
        InitializeWebView();
    }

    private void InitializeForm()
    {
        Text = "Bug 提交器";
        Size = new Size(500, 605);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Shield;

        FormClosing += (_, e) =>
        {
            // WebView2 的 Dispose 会死锁，直接杀进程
            e.Cancel = false;
            Environment.Exit(0);
        };
    }

    private async void InitializeWebView()
    {
        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

            // 隐藏滚动条
            await _webView.CoreWebView2.ExecuteScriptAsync(
                "document.documentElement.style.overflow = 'hidden';" +
                "document.body.style.overflow = 'hidden';");
            _webView.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 初始化失败，请确保已安装 Microsoft Edge WebView2 运行时。\n\n{ex.Message}",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
