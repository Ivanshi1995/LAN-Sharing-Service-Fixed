using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentFTP;
using Microsoft.AspNetCore.Http.Features;
using MySqlConnector;

namespace BugUploader;

public static class Program
{
    // ============================================================
    // 加密凭证 (AES-256-CBC) - 与 server.py / api.php 完全一致
    // ============================================================
    private static readonly Dictionary<string, string> EncryptedConfig = new()
    {
        ["DB_HOST"] = "+EG2wVQY+x1Hhu8EotPRz2zlvUeASstglKegoo/eDjA=",
        ["DB_PORT"] = "a4+hQaBOvLR3/xD/UZutopJfA/Tq/TmMJjTWNtsZSsk=",
        ["DB_NAME"] = "TsYtJQjEI9cI3LZFwWSHd0wLUDl2//5wOuB0vHiHe906czs2MIBpUBU6UdpOUVh+",
        ["DB_USER"] = "X2LToYwwSufdbB31V5h7cKh1aEFKjkZiCwYF/5n7jtIyOPlAHA2t5/99t3PyEBk+",
        ["DB_PASS"] = "gnGmjFukGrlxDUoh/78m5IMupLQL1qk20+qtIDJRv6GsLndGqBt5jOYEmQKfCoZF",
        ["FTP_HOST"] = "knr2UcW9Yx35V65rv84lB/xHcDlX4QqDfwVaJnuwctY=",
        ["FTP_PORT"] = "kA+NLE1KsaTFiH4uLT5/z5ZUxpjAsBQQq0vO9E7T9VQ=",
        ["FTP_USER"] = "YrgVIJQQV+mdeypxIdDqPTazHOzxrrZOpJV084GK0lQ=",
        ["FTP_PASS"] = "4pWRzeyEvrBXeUYBJiMHxPZPQRJmtkbNuP66zefcVUk=",
    };

    // ============================================================
    // 密钥重建 - 与 server.py _k() 完全一致
    // ============================================================
    private static readonly byte[] AesKey = BuildKey();

    private static byte[] BuildKey()
    {
        var parts = new byte[8][];
        parts[3] = new byte[] { 0x34, 0x2d, 0xab, 0x21 };
        parts[6] = new byte[] { 0xa7, 0x25, 0xf6, 0x5b };
        parts[0] = new byte[] { 0xab, 0x96, 0x0b, 0x59 };
        parts[5] = new byte[] { 0x47, 0xa0, 0xc2, 0x2c };
        parts[7] = new byte[] { 0x71, 0xd8, 0xf9, 0x42 };
        parts[1] = new byte[] { 0x4a, 0x8a, 0x28, 0x89 };
        parts[4] = new byte[] { 0x8d, 0xf9, 0xa3, 0x34 };
        parts[2] = new byte[] { 0x16, 0x83, 0x30, 0xfb };

        var key = new byte[32];
        int pos = 0;
        for (int i = 0; i < 8; i++)
        {
            Array.Copy(parts[i], 0, key, pos, 4);
            pos += 4;
        }
        return key;
    }

    // ============================================================
    // AES-256-CBC 解密
    // ============================================================
    private static string DecryptConfig(string base64Cipher)
    {
        var raw = Convert.FromBase64String(base64Cipher);
        var iv = raw[..16];
        var ciphertext = raw[16..];

        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    // ============================================================
    // 解密所有配置
    // ============================================================
    private static (
        string dbHost, int dbPort, string dbName, string dbUser, string dbPass,
        string ftpHost, int ftpPort, string ftpUser, string ftpPass
    ) LoadConfig()
    {
        return (
            DecryptConfig(EncryptedConfig["DB_HOST"]),
            int.Parse(DecryptConfig(EncryptedConfig["DB_PORT"])),
            DecryptConfig(EncryptedConfig["DB_NAME"]),
            DecryptConfig(EncryptedConfig["DB_USER"]),
            DecryptConfig(EncryptedConfig["DB_PASS"]),
            DecryptConfig(EncryptedConfig["FTP_HOST"]),
            int.Parse(DecryptConfig(EncryptedConfig["FTP_PORT"])),
            DecryptConfig(EncryptedConfig["FTP_USER"]),
            DecryptConfig(EncryptedConfig["FTP_PASS"])
        );
    }

    // ============================================================
    // 运行时变量
    // ============================================================
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

    // 持久化冷却文件（关闭窗口后冷却状态不丢失）
    private static readonly string CooldownFile = Path.Combine(BaseDir, "cooldown.dat");

    private const int MaxImages = 1;
    private const long MaxImageSize = 10 * 1024 * 1024; // 10MB
    private const int RateLimitSeconds = 60;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // 解密后的配置（启动时加载）
    private static string _dbHost = "", _dbName = "", _dbUser = "", _dbPass = "";
    private static int _dbPort;
    private static string _ftpHost = "", _ftpUser = "", _ftpPass = "";
    private static int _ftpPort;

    // 最近一次提交的 UTC 时间
    private static DateTime _lastSubmitUtc = DateTime.MinValue;
    private static readonly object _cooldownLock = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // 解密配置
        try
        {
            (_dbHost, _dbPort, _dbName, _dbUser, _dbPass,
             _ftpHost, _ftpPort, _ftpUser, _ftpPass) = LoadConfig();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配置解密失败，可能是密钥或加密数据损坏。\n\n{ex.Message}",
                "Bug 提交器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 恢复持久化冷却状态
        LoadCooldown();

        var port = FindFreePort(8380);
        var url = $"http://127.0.0.1:{port}";

        // 构建 Kestrel 服务器
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = (MaxImages + 1) * MaxImageSize + 1024 * 1024;
        });
        builder.Services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = (MaxImages + 1) * MaxImageSize + 1024 * 1024;
            o.ValueCountLimit = 20;
        });
        builder.WebHost.SuppressStatusMessages(true);

        var app = builder.Build();

        // === API 端点 ===

        // 版本列表代理（绕过跨域）
        app.MapGet("/api/versions", async ctx =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var text = await http.GetStringAsync("https://lan.sharing.service.lahong.top/Version_history.json");
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(text);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync($"加载失败: {ex.Message}");
            }
        });

        // 首页
        app.MapGet("/", async ctx =>
        {
            var htmlPath = Path.Combine(BaseDir, "bug_upload.html");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await ctx.Response.SendFileAsync(htmlPath);
        });

        // 频率限制状态
        app.MapGet("/api/ratelimit", ctx =>
        {
            int remainingSeconds;
            lock (_cooldownLock)
            {
                var elapsed = (DateTime.UtcNow - _lastSubmitUtc).TotalSeconds;
                remainingSeconds = elapsed >= RateLimitSeconds ? 0
                    : (int)Math.Ceiling(RateLimitSeconds - elapsed);
            }

            return ctx.Response.WriteAsJsonAsync(new
            {
                can_submit = remainingSeconds == 0,
                remaining_seconds = remainingSeconds
            }, JsonOpts);
        });

        // 提交 Bug
        app.MapPost("/api", async ctx =>
        {
            // 频率限制检查（基于持久化文件）
            lock (_cooldownLock)
            {
                var elapsed = (DateTime.UtcNow - _lastSubmitUtc).TotalSeconds;
                if (elapsed < RateLimitSeconds)
                {
                    var wait = (int)Math.Ceiling(RateLimitSeconds - elapsed);
                    ctx.Response.StatusCode = 429;
                    ctx.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"提交过于频繁，请等待 {wait} 秒后再试"
                    }, JsonOpts);
                    return;
                }
            }

            // 解析表单
            var form = await ctx.Request.ReadFormAsync();
            var title = form["title"].FirstOrDefault()?.Trim() ?? "";
            var version = form["version"].FirstOrDefault()?.Trim() ?? "";
            var severity = form["severity"].FirstOrDefault()?.Trim() ?? "一般";
            var content = form["content"].FirstOrDefault()?.Trim() ?? "";

            // 校验必填项
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(content))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                    { success = false, message = "请填写所有必填项（标题、版本、描述）" }, JsonOpts);
                return;
            }

            if (title.Length > 500)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                    { success = false, message = "标题过长（最多500字符）" }, JsonOpts);
                return;
            }
            if (version.Length > 100)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new
                    { success = false, message = "版本号过长（最多100字符）" }, JsonOpts);
                return;
            }

            // 处理截图上传到 FTP（单张）
            var screenshotFiles = form.Files.GetFiles("screenshots");
            string? ftpPath = null;

            if (screenshotFiles.Count > 0)
            {
                var file = screenshotFiles[0];

                // 校验文件类型
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new
                        { success = false, message = $"不支持的文件类型: {ext}，仅限 PNG/JPG/GIF/WebP" }, JsonOpts);
                    return;
                }

                if (file.Length > MaxImageSize)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new
                        { success = false, message = $"文件过大（最大 10MB）" }, JsonOpts);
                    return;
                }

                // Magic bytes 校验
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();

                if (!IsValidImage(bytes))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new
                        { success = false, message = "文件不是有效的图片" }, JsonOpts);
                    return;
                }

                // 上传到 FTP
                var remoteName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Random.Shared.Next(1000, 9999)}{ext}";
                try
                {
                    using var ftp = new FtpClient(_ftpHost, _ftpUser, _ftpPass, _ftpPort);
                    ftp.Config.DataConnectionType = FtpDataConnectionType.PASV;
                    ftp.Connect();

                    if (!ftp.DirectoryExists("bug_screenshots"))
                        ftp.CreateDirectory("bug_screenshots");

                    ftpPath = $"bug_screenshots/{remoteName}";
                    ftp.UploadBytes(bytes, ftpPath);
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new
                        { success = false, message = $"截图上传失败: {ex.Message}" }, JsonOpts);
                    return;
                }
            }

            // 插入 MySQL 数据库
            int insertId;
            try
            {
                var connStr = $"Server={_dbHost};Port={_dbPort};Database={_dbName};User={_dbUser};Password={_dbPass};Charset=utf8mb4;";
                using var conn = new MySqlConnection(connStr);
                await conn.OpenAsync();

                // 自动建表
                const string createTable = """
                    CREATE TABLE IF NOT EXISTS `bug_reports` (
                        `id` INT AUTO_INCREMENT PRIMARY KEY,
                        `title` VARCHAR(500) NOT NULL COMMENT 'Bug标题',
                        `version` VARCHAR(100) NOT NULL COMMENT '软件版本',
                        `severity` VARCHAR(50) NOT NULL DEFAULT '一般' COMMENT '严重程度',
                        `content` TEXT NOT NULL COMMENT '详细描述',
                        `screenshot` TEXT DEFAULT NULL COMMENT '截图FTP路径(多个用|分隔)',
                        `created_at` DATETIME DEFAULT CURRENT_TIMESTAMP COMMENT '提交时间'
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Bug报告表'
                    """;
                using var createCmd = new MySqlCommand(createTable, conn);
                await createCmd.ExecuteNonQueryAsync();

                // 插入记录
                const string insert = """
                    INSERT INTO `bug_reports` (`title`, `version`, `severity`, `content`, `screenshot`, `created_at`)
                    VALUES (@title, @version, @severity, @content, @screenshot, NOW());
                    SELECT LAST_INSERT_ID();
                    """;
                using var insertCmd = new MySqlCommand(insert, conn);
                insertCmd.Parameters.AddWithValue("@title", title);
                insertCmd.Parameters.AddWithValue("@version", version);
                insertCmd.Parameters.AddWithValue("@severity", severity);
                insertCmd.Parameters.AddWithValue("@content", content);
                insertCmd.Parameters.AddWithValue("@screenshot", (object?)ftpPath ?? DBNull.Value);

                insertId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new
                    { success = false, message = $"数据库保存失败: {ex.Message}" }, JsonOpts);
                return;
            }

            // 记录频率限制（持久化到文件）
            SaveCooldown(DateTime.UtcNow);

            await ctx.Response.WriteAsJsonAsync(new
            {
                success = true,
                message = "Bug 报告提交成功！",
                data = new
                {
                    id = insertId,
                    title,
                    has_screenshot = ftpPath != null
                }
            }, JsonOpts);
        });

        // 启动服务器
        var serverReady = new TaskCompletionSource<bool>();
        var cts = new CancellationTokenSource();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
                lifetime.ApplicationStarted.Register(() => serverReady.TrySetResult(true));
                await app.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                serverReady.TrySetException(ex);
            }
        }, cts.Token);

        if (!serverReady.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            MessageBox.Show("服务器启动超时，请重试", "Bug 提交器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 启动 WinForms + WebView2
        // 进程退出由 MainForm.FormClosed 中的 Environment.Exit(0) 保证
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(url));
    }

    // ============================================================
    // 持久化冷却：关闭窗口后冷却状态不丢失
    // ============================================================
    private static void LoadCooldown()
    {
        try
        {
            if (File.Exists(CooldownFile))
            {
                var text = File.ReadAllText(CooldownFile, Encoding.UTF8);
                if (long.TryParse(text, out var ticks) && ticks > 0)
                {
                    _lastSubmitUtc = new DateTime(ticks, DateTimeKind.Utc);
                }
            }
        }
        catch
        {
            // 文件损坏则忽略，从零开始
        }
    }

    private static void SaveCooldown(DateTime utcTime)
    {
        lock (_cooldownLock)
        {
            _lastSubmitUtc = utcTime;
            try
            {
                File.WriteAllText(CooldownFile, utcTime.Ticks.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 写入失败不影响提交结果
            }
        }
    }

    // ============================================================
    // Magic bytes 校验
    // ============================================================
    private static bool IsValidImage(byte[] bytes)
    {
        if (bytes.Length < 4) return false;

        // PNG: 89 50 4E 47
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return true;

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;

        // GIF: 47 49 46 38
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return true;

        // WebP: 52 49 46 46 ... 57 45 42 50
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes.Length >= 12
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return true;

        return false;
    }

    private static int FindFreePort(int start)
    {
        for (int port = start; port < start + 1000; port++)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch
            {
                // 端口占用，尝试下一个
            }
        }
        throw new InvalidOperationException("找不到可用端口");
    }
}
