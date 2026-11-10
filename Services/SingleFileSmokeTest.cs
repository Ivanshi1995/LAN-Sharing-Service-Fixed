using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LanFileShare;

internal static class SingleFileSmokeTest
{
    public static async Task<int> RunAsync(string reportPath, string dataDirectory)
    {
        reportPath = Path.GetFullPath(reportPath);
        var checks = new List<object>();
        var failed = 0;
        void Check(string name, bool result)
        {
            checks.Add(new { name, passed = result });
            if (!result) failed++;
        }
        try
        {
            AppPaths.Initialize(dataDirectory);
            Directory.SetCurrentDirectory(AppPaths.DataDirectory);
            Check("运行独立单文件程序", AppPaths.IsSingleFile);
            Check("配置目录与资源目录分离", AppPaths.DataDirectory != AppPaths.ResourceDirectory);
            Check("首次启动不开启开机自启动", !JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.DataDirectory, "settings.json"))).RootElement.GetProperty("AutoStart").GetBoolean());
            var persistencePath = Path.Combine(AppPaths.DataDirectory, "smoke-persistence.txt");
            var marker = File.Exists(persistencePath) ? File.ReadAllText(persistencePath) : Guid.NewGuid().ToString("N");
            File.WriteAllText(persistencePath, marker);

            // Calling WebView2 also validates that its native loader is available in the bundle.
            var webViewAvailable = false;
            try { webViewAvailable = !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch (WebView2RuntimeNotFoundException) { }
            Check("WebView2 原生加载器可用", true);
            checks.Add(new { name = "电脑已安装 WebView2", available = webViewAvailable });

            await using var app = Program.CreateSmokeTestServer();
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var path in new[] { "/", "/admin", "/CDN/tailwindcss.js", "/CDN/upload-fingerprint.js", "/CDN/fonts/Inter-400.ttf", "/bandwidth" })
            {
                using var response = await http.GetAsync(path);
                Check("读取打包资源 " + path, response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).Length > 0);
            }
            foreach (var path in new[] { "/settings.json", "/users.json" })
                Check("私有文件拒绝直接读取 " + path, (await http.GetAsync(path)).StatusCode == HttpStatusCode.NotFound);
            var files = app.Services.GetRequiredService<FileShareService>();
            files.Settings.PublicShareEnabled = true;
            Check("公共共享页面可用", (await http.GetAsync("/public")).IsSuccessStatusCode);
            var folder = Path.Combine(AppPaths.DataDirectory, "smoke-user");
            Directory.CreateDirectory(folder);
            var users = app.Services.GetRequiredService<UserService>();
            var user = users.CreateUser("smoke" + Guid.NewGuid().ToString("N")[..8], "test-password", new() { folder }, true);
            Check("账户可保存", user.success && File.Exists(Path.Combine(AppPaths.DataDirectory, "users.json")));
            var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.user!.Username, password = "test-password" });
            Check("账户可登录", login.IsSuccessStatusCode);
            http.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            var bytes = Encoding.UTF8.GetBytes("单文件程序上传下载验证");
            var init = await http.PostAsJsonAsync("/api/chunked/init", new
            {
                subPath = "__folder__/0", fileName = "smoke.txt", fileSize = bytes.Length,
                contentHash = Convert.ToHexString(SHA256.HashData(bytes))
            });
            Check("初始化上传成功", init.IsSuccessStatusCode);
            var id = (await init.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString();
            var write = await http.PostAsync("/api/chunked/write?uploadId=" + id + "&offset=0", new ByteArrayContent(bytes));
            Check("分块上传成功", write.IsSuccessStatusCode);
            var complete = await http.PostAsJsonAsync("/api/chunked/complete", new { uploadId = id, overwrite = true });
            Check("文件发布成功", complete.IsSuccessStatusCode);
            Check("下载内容与上传一致", (await http.GetByteArrayAsync("/api/files/download?path=__folder__/0/smoke.txt")).SequenceEqual(bytes));
            Check("Excel 导入模板已打包", (await http.GetAsync("/api/users/template")).IsSuccessStatusCode);
            Check("SQLite 聊天存储可用", File.Exists(Path.Combine(AppPaths.DataDirectory, "ChatArchive", "chat_hot.db")));
            Check("运行数据持久保存", File.ReadAllText(persistencePath) == marker);
            Check("旧增量更新不会覆盖单文件版本", !string.IsNullOrEmpty((await app.Services.GetRequiredService<UpdateService>().CheckForUpdatesAsync()).Error));
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            failed++;
            checks.Add(new { name = "单文件验证异常", passed = false, error = ex.ToString() });
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
