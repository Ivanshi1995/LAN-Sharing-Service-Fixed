using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using LanFileShare;
using LanFileShare.UI;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using QRCoder;

namespace LanFileShare;

public class Program
{
    private static FileShareService? _service;
    private static UserService? _userService;
    private static ConcurrentBag<StreamWriter>? _sseClients;
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;
    private static bool _fileShareEnabled = true;
    private static SynchronizationContext? _uiContext;
    private static MainForm? _mainForm;
    private static readonly SemaphoreSlim _nativeFolderPicker = new(1, 1);
    private static int _activeListenPort = 8080;
    
    // 聊天消息 SSE 客户端：key = username, value = StreamWriter
    private static readonly ConcurrentDictionary<string, ConcurrentBag<StreamWriter>> _chatClients = new();
    private static ChatStorageService? _chatStorage;
    private static readonly string _adminToken = Guid.NewGuid().ToString("N");

    private static string AppBaseDir => AppPaths.DataDirectory;

    private static int LoadListenPort()
    {
        const int defaultPort = 8080;
        try
        {
            var settingsPath = Path.Combine(AppBaseDir, "settings.json");
            if (!File.Exists(settingsPath))
                return defaultPort;

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<FileShareSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return settings is { Port: > 0 and <= 65535 } ? settings.Port : defaultPort;
        }
        catch
        {
            return defaultPort;
        }
    }

    private static string LoadSelectedInterfaceIP()
    {
        try
        {
            var settingsPath = Path.Combine(AppBaseDir, "settings.json");
            if (!File.Exists(settingsPath)) return "";
            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("selectedInterface", out var si) &&
                si.TryGetProperty("ip", out var ipEl))
            {
                return ipEl.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static bool RestartApplication()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--restart",
            WorkingDirectory = AppBaseDir,
            UseShellExecute = true
        });
        UpdateService.ScheduleProcessExit(_cts, _mainForm);
        return true;
    }

    private static bool TryAcquireSingleInstanceMutex(bool waitForRelease, out Mutex? mutex)
    {
        mutex = null;
        var attempts = waitForRelease ? 50 : 1;
        for (var i = 0; i < attempts; i++)
        {
            var m = new Mutex(true, "LanFileShare_SingleInstance", out var createdNew);
            if (createdNew)
            {
                mutex = m;
                return true;
            }
            m.Dispose();
            if (!waitForRelease)
                return false;
            Thread.Sleep(200);
        }
        return false;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--smoke-test")
        {
            Environment.ExitCode = SingleFileSmokeTest.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
            return;
        }
        var isRestart = args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase));
        if (!TryAcquireSingleInstanceMutex(isRestart, out var mutex) || mutex == null)
        {
            MessageBox.Show("LanFileShare 已经在运行中，请查看系统托盘图标。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (mutex)
        {
            RunApplication(args);
        }
    }

    private static void RunApplication(string[] args)
    {
        var dataArgument = Array.IndexOf(args, "--data-dir");
        AppPaths.Initialize(dataArgument >= 0 && dataArgument + 1 < args.Length ? args[dataArgument + 1] : null);
        Directory.SetCurrentDirectory(AppBaseDir);
        ApplicationConfiguration.Initialize();

        var listenPort = LoadListenPort();
        _activeListenPort = listenPort;
        var selectedIP = LoadSelectedInterfaceIP();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppBaseDir,
            WebRootPath = Path.Combine(AppPaths.ResourceDirectory, "wwwroot")
        });
        builder.WebHost.UseKestrel(options =>
        {
            if (!string.IsNullOrEmpty(selectedIP) && System.Net.IPAddress.TryParse(selectedIP, out var bindIP))
            {
                options.Listen(bindIP, listenPort);
            }
            else
            {
                options.ListenAnyIP(listenPort);
            }
            options.Limits.MaxRequestBodySize = 200L * 1024 * 1024 * 1024;
        });
        builder.Services.AddSingleton<FileShareService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<UploadAccessPolicy>();
        builder.Services.AddSingleton<ChunkedUploadService>();
        builder.Services.AddSingleton<ConcurrentBag<StreamWriter>>();
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 200L * 1024 * 1024 * 1024;
            options.ValueLengthLimit = int.MaxValue;
            options.MultipartHeadersLengthLimit = int.MaxValue;
            options.MemoryBufferThreshold = 10 * 1024 * 1024;
        });

        var app = builder.Build();
        _service = app.Services.GetRequiredService<FileShareService>();
        _sseClients = app.Services.GetRequiredService<ConcurrentBag<StreamWriter>>();

        ConfigureApp(app);

        if (_service.Settings.Port != listenPort)
        {
            _service.Settings.Port = listenPort;
            _service.SaveSettings(_service.Settings);
        }

        var primaryIP = _service.GetPrimaryIP();
        var serverUrl = _service.GetServerUrl(primaryIP, listenPort);
        var clientUrl = _service.GetClientUrl(primaryIP, listenPort);
        var localAdminUrl = $"{_service.GetServerUrl("127.0.0.1", listenPort)}/admin?admin={_adminToken}";

        _service.AddLog("INFO", "服务端", $"已启动 {serverUrl}");

        _service.StartHeartbeatCleanup();
        _service.EnsurePublicShareDirectory();

        // 后台预构建所有文件索引，加速首次搜索
        Task.Run(() => BuildAllStartupIndices());

        // SSE keep-alive
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(25000);
                if (_sseClients == null) continue;
                foreach (var client in _sseClients)
                {
                    try { await client.WriteAsync(": ping\n\n"); await client.FlushAsync(); }
                    catch { }
                }
            }
        });

        // 在独立后台线程启动 Kestrel
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => app.RunAsync(_cts.Token));

        using var form = new MainForm(localAdminUrl, _service.Settings.MinimizeToTray, LoadIcon(), _service.Settings, _service, args.Contains("--browser"));
        _mainForm = form;
        form.Shown += (_, _) =>
        {
            _uiContext ??= SynchronizationContext.Current;
            // 开机自启时自动最小化到系统托盘
            if (_service.Settings.MinimizeToTray)
                form.AutoMinimizeToTrayIfEnabled();
        };

        Application.Run(form);

        // 退出时仅保存窗口大小，位置每次启动时居中显示
        if (form.WindowState != FormWindowState.Minimized)
        {
            _service.Settings.WindowWidth = form.Width;
            _service.Settings.WindowHeight = form.Height;
        }
        _service.SaveSettings(_service.Settings);

        // 关闭服务
        _service.StopHeartbeatCleanup();
        _cts.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    internal static WebApplication CreateSmokeTestServer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(), ContentRootPath = AppBaseDir
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<FileShareService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<UploadAccessPolicy>();
        builder.Services.AddSingleton<ChunkedUploadService>();
        builder.Services.AddSingleton<ConcurrentBag<StreamWriter>>();
        var app = builder.Build();
        _service = app.Services.GetRequiredService<FileShareService>();
        _sseClients = app.Services.GetRequiredService<ConcurrentBag<StreamWriter>>();
        ConfigureApp(app);
        return app;
    }

    internal static void ConfigureApp(WebApplication app)
    {
        var service = _service!;
        var sseClients = _sseClients!;
        _userService = app.Services.GetRequiredService<UserService>();
        var userService = _userService;
        var authService = app.Services.GetRequiredService<AuthService>();
        var updateService = app.Services.GetRequiredService<UpdateService>();
        var chunkedUploadService = app.Services.GetRequiredService<ChunkedUploadService>();
        _chatStorage = new ChatStorageService(AppBaseDir);
        service.OnHeartbeatCleanup = () => authService.CleanupOnlineStale();
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // SSE broadcast
        service.OnLogAdded += (entry) =>
        {
            var json = JsonSerializer.Serialize(entry, jsonOpts);
            var data = $"data: {json}\n\n";
            foreach (var client in sseClients)
            {
                try { client.WriteAsync(data).Wait(100); client.FlushAsync().Wait(100); }
                catch { }
            }
        };

        // 只公开离线资源；配置、账户和共享文件必须经过各自 API。
        app.UseLanStaticAssets(AppPaths.ResourceDirectory);

        // 全局：所有 API 响应禁用缓存
        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "-1";
            await next(ctx);
        });

        // HTML pages – 登录页（所有人可访问），管理页仅限本机 WebView2 携带有效 token 访问
        app.MapGet("/", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; ctx.Response.ContentType = "text/plain; charset=utf-8"; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "lan-file-share-client.html"));
        });

        // 管理页面（仅管理端/管理员可访问）
        app.MapGet("/admin", async (HttpContext ctx) =>
        {
            var isLocal = ctx.Connection.RemoteIpAddress != null &&
                          System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);

            // 方式1: 本地回环 → 直接放行（只有本机 WebView2 能发本地回环请求）
            if (isLocal)
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "lan-file-share-server.html"));
                return;
            }

            // 方式2: 远程管理员用户通过 session 登录
            var session = authService.GetSession(ctx);
            if (session != null)
            {
                var user = userService.FindById(session.UserId);
                if (user != null && user.IsAdmin)
                {
                    service.AddLog("INFO", "服务端", $"管理员远程访问 {ctx.Connection.RemoteIpAddress} ({session.Username})");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "lan-file-share-server.html"));
                    return;
                }
            }

            // 未认证 → 重定向到登录页，登录后跳回 /admin
            ctx.Response.Redirect($"/?redirect={Uri.EscapeDataString("/admin")}");
        });

        // 保留 /client 跳转到登录页（兼容旧链接）
        app.MapGet("/client", (HttpContext ctx) =>
        {
            ctx.Response.Redirect("/", permanent: true);
            return Task.CompletedTask;
        });

        app.MapGet("/public", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; ctx.Response.ContentType = "text/plain; charset=utf-8"; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; ctx.Response.ContentType = "text/plain; charset=utf-8"; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "lan-file-share-public.html"));
        });

        // 带宽管理页面（仅管理端/管理员可访问）
        app.MapGet("/bandwidth", async (HttpContext ctx) =>
        {
            // 方式1: 本地回环 + admin token 验证（WebView2 管理端）
            var isLocal = ctx.Connection.RemoteIpAddress != null &&
                          System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);
            if (isLocal && ctx.Request.Query.TryGetValue("admin", out var token) && token == _adminToken)
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "LAN Management", "Bandwidth Management.html"));
                return;
            }

            // 方式2: 远程管理员用户通过 session 登录
            var session = authService.GetSession(ctx);
            if (session != null)
            {
                var user = userService.FindById(session.UserId);
                if (user != null && user.IsAdmin)
                {
                    service.AddLog("INFO", "服务端", $"管理员远程访问带宽管控 {ctx.Connection.RemoteIpAddress} ({session.Username})");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.SendFileAsync(Path.Combine(AppPaths.ResourceDirectory, "LAN Management", "Bandwidth Management.html"));
                    return;
                }
            }

            // 未认证 → 重定向到登录页，登录后跳回 /bandwidth
            ctx.Response.Redirect($"/?redirect={Uri.EscapeDataString("/bandwidth")}");
        });

        // API: heartbeat (客户端保活 + 服务端实时连接统计)
        app.MapPost("/api/heartbeat", (HttpContext ctx) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd))
                ip = fwd.ToString();
            service.RecordHeartbeat(ip);
            var session = authService.GetSession(ctx);
            if (session != null)
            {
                authService.TouchOnline(session, ip);
                service.UpdateUserOnline(session.UserId, session.Username, ip);
            }
            return Results.Json(new { ok = true });
        });

        // API: 聊天消息发送
        app.MapPost("/api/chat/send", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var to = root.GetProperty("to").GetString() ?? "";
            var type = root.GetProperty("type").GetString() ?? "text";
            var content = root.GetProperty("content").GetString() ?? "";
            var fileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
            var fileUrl = root.TryGetProperty("fileUrl", out var fu) ? fu.GetString() : null;
            
            var msg = new Dictionary<string, object?>
            {
                ["from"] = session.Username,
                ["type"] = type,
                ["content"] = content,
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (fileName != null) msg["fileName"] = fileName;
            if (fileUrl != null) msg["fileUrl"] = fileUrl;
            
            // 广播给目标用户的所有 SSE 连接
            if (_chatClients.TryGetValue(to, out var targetClients))
            {
                var json = JsonSerializer.Serialize(msg, jsonOpts);
                var data = $"data: {json}\n\n";
                foreach (var client in targetClients)
                {
                    try { await client.WriteAsync(data); await client.FlushAsync(); }
                    catch { }
                }
            }

            // 持久化消息到存储
            _chatStorage?.SaveMessage(
                sender: session.Username,
                recipient: to,
                type: type,
                content: content,
                fileName: fileName,
                fileUrl: fileUrl,
                time: (long)msg["time"]!
            );

            return Results.Json(new { ok = true });
        });

        // API: 聊天消息 SSE 流（客户端订阅）
        app.MapGet("/api/chat/stream", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; return; }
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("Connection", "keep-alive");
            var sw = new StreamWriter(ctx.Response.Body);
            var clients = _chatClients.GetOrAdd(session.Username, _ => new ConcurrentBag<StreamWriter>());
            clients.Add(sw);
            try
            {
                // 发送初始化事件
                await sw.WriteAsync("event: connected\ndata: {\"ok\":true}\n\n");
                await sw.FlushAsync();
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(30000, ctx.RequestAborted);
                    await sw.WriteAsync(": keepalive\n\n");
                    await sw.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                var bag = _chatClients.GetOrAdd(session.Username, _ => new ConcurrentBag<StreamWriter>());
                var items = bag.ToList();
                bag = new ConcurrentBag<StreamWriter>(items.Where(x => x != sw));
                _chatClients[session.Username] = bag;
            }
        });

        // ========== 聊天文件传输模块（独立于聊天消息通道）==========
        var chatFilesDir = Path.Combine(AppBaseDir, "SharedFiles", "ChatFiles");
        Directory.CreateDirectory(chatFilesDir);

        // 聊天文件元信息存储：fileId -> { fileName, fileSize, filePath, uploader, uploadTime }
        var _chatFileMetas = new ConcurrentDictionary<string, ChatFileMeta>();

        // API: 聊天文件上传（multipart/form-data）
        app.MapPost("/api/chat/upload", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { error = "请使用 multipart/form-data" }, statusCode: 400);

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.Json(new { error = "未选择文件" }, statusCode: 400);

            // 限制单文件 100MB
            if (file.Length > 100L * 1024 * 1024)
                return Results.Json(new { error = "文件大小不能超过 100MB" }, statusCode: 400);

            var fileId = Guid.NewGuid().ToString("N");
            var ext = Path.GetExtension(file.FileName);
            var safeFileName = fileId + ext;
            var savePath = Path.Combine(chatFilesDir, safeFileName);

            await using (var stream = new FileStream(savePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var meta = new ChatFileMeta
            {
                FileId = fileId,
                FileName = file.FileName,
                FileSize = file.Length,
                FilePath = savePath,
                Uploader = session.Username,
                UploadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ContentType = file.ContentType ?? "application/octet-stream"
            };
            _chatFileMetas[fileId] = meta;

            return Results.Json(new
            {
                ok = true,
                fileId,
                fileName = file.FileName,
                fileSize = file.Length,
                downloadUrl = $"/api/chat/download/{fileId}"
            });
        }).DisableAntiforgery();

        // API: 聊天文件下载
        app.MapGet("/api/chat/download/{fileId}", async (string fileId, HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; return; }

            if (!_chatFileMetas.TryGetValue(fileId, out var meta))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("文件不存在或已过期");
                return;
            }

            if (!System.IO.File.Exists(meta.FilePath))
            {
                _chatFileMetas.TryRemove(fileId, out _);
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("文件不存在或已过期");
                return;
            }

            ctx.Response.ContentType = meta.ContentType;
            ctx.Response.Headers.Append("Content-Disposition",
                $"attachment; filename=\"{Uri.EscapeDataString(meta.FileName)}\"");
            ctx.Response.ContentLength = meta.FileSize;
            await ctx.Response.SendFileAsync(meta.FilePath);
        });

        // API: 聊天文件元信息查询
        app.MapGet("/api/chat/file/{fileId}", (string fileId, HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!_chatFileMetas.TryGetValue(fileId, out var meta))
                return Results.Json(new { error = "文件不存在" }, statusCode: 404);

            return Results.Json(new
            {
                fileId = meta.FileId,
                fileName = meta.FileName,
                fileSize = meta.FileSize,
                contentType = meta.ContentType,
                uploader = meta.Uploader,
                uploadTime = meta.UploadTime,
                downloadUrl = $"/api/chat/download/{fileId}"
            });
        });

        // ========== 聊天历史记录 API ==========

        // API: 查询两个用户之间的历史消息
        app.MapGet("/api/chat/history", (HttpContext ctx, string with, int limit, long? before) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            if (string.IsNullOrWhiteSpace(with))
                return Results.Json(new { error = "缺少参数 with" }, statusCode: 400);

            if (limit <= 0 || limit > 500) limit = 100;
            var beforeVal = (before ?? 0) <= 0 ? long.MaxValue : before.Value;

            try
            {
                var messages = _chatStorage?.QueryMessages(session.Username, with, limit, beforeVal) ?? new();
                var result = messages.Select(m => new
                {
                    type = m.MsgType,
                    content = m.Content,
                    from = m.Sender,
                    isSelf = m.Sender == session.Username,
                    time = m.CreatedAt,
                    fileName = m.FileName,
                    fileUrl = m.FileUrl,
                    fileSize = m.FileSize
                });
                return Results.Json(new { ok = true, messages = result });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // API: 获取与当前用户有过对话的用户列表
        app.MapGet("/api/chat/partners", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            var partners = _chatStorage?.GetConversationPartners(session.Username) ?? new();
            return Results.Json(new { ok = true, partners });
        });

        // API: 全局搜索聊天记录
        app.MapGet("/api/chat/search", (HttpContext ctx, string? keyword, string? sender,
            string? recipient, long? start, long? end, int limit) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (limit <= 0 || limit > 500) limit = 200;

            try
            {
                // 只允许搜索自己相关的消息
                var messages = _chatStorage?.SearchMessages(
                    keyword: keyword,
                    sender: sender ?? session.Username,
                    recipient: recipient,
                    startTime: start,
                    endTime: end,
                    limit: limit) ?? new();

                // 只返回当前用户参与的消息
                messages = messages.Where(m =>
                    m.Sender == session.Username || m.Recipient == session.Username).ToList();

                var result = messages.Select(m => new
                {
                    type = m.MsgType,
                    content = m.Content,
                    from = m.Sender,
                    to = m.Recipient,
                    isSelf = m.Sender == session.Username,
                    time = m.CreatedAt,
                    fileName = m.FileName,
                    fileUrl = m.FileUrl,
                    fileSize = m.FileSize,
                    partner = m.Sender == session.Username ? m.Recipient : m.Sender
                });
                return Results.Json(new { ok = true, messages = result });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // API: 获取归档日期列表
        app.MapGet("/api/chat/archive-dates", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            try
            {
                var dates = _chatStorage?.ListArchiveDates() ?? new();
                return Results.Json(new { ok = true, dates = dates.Select(d => d.ToString("yyyy-MM-dd")) });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // API: 从归档加载指定日期的对话记录
        app.MapGet("/api/chat/archive-load", (HttpContext ctx, string with, string date) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
            if (!DateTime.TryParse(date, out var dt))
                return Results.Json(new { error = "日期格式错误" }, statusCode: 400);

            try
            {
                var messages = _chatStorage?.LoadFromArchive(session.Username, with, dt) ?? new();
                var result = messages.Select(m => new
                {
                    type = m.MsgType,
                    content = m.Content,
                    from = m.Sender,
                    isSelf = m.Sender == session.Username,
                    time = m.CreatedAt,
                    fileName = m.FileName,
                    fileUrl = m.FileUrl,
                    fileSize = m.FileSize
                });
                return Results.Json(new { ok = true, date = date, messages = result });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // API: status（仅管理端/管理员）
        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var (up, down, totalUp, totalDown) = service.GetStats();
            var primaryIP = service.GetPrimaryIP();
            var port = service.Settings.Port;
            var onlineUsers = authService.GetOnlineUsers();
            return Results.Json(new
            {
                running = _fileShareEnabled,
                port,
                activePort = _activeListenPort,
                serverUrl = service.GetServerUrl(primaryIP, port),
                clientUrl = service.GetClientUrl(primaryIP, port),
                publicShareUrl = service.GetPublicShareUrl(primaryIP, port),
                publicShareEnabled = service.Settings.PublicShareEnabled,
                publicShareReadOnly = service.Settings.PublicShareReadOnly,
                publicShareDirectories = service.GetPublicSharePaths(),
                nativeFolderPicker = CanUseNativeFolderPicker(ctx),
                activeConnections = service.ActiveConnections,
                uniqueDevices = service.UniqueDeviceCount,
                onlineUsers = onlineUsers,
                onlineUserCount = onlineUsers.Count,
                uploadSpeed = FormatSpeed(up),
                downloadSpeed = FormatSpeed(down),
                rawUploadSpeed = up,
                rawDownloadSpeed = down,
                totalUploaded = FormatSize(totalUp),
                totalDownloaded = FormatSize(totalDown),
                cacheTotalBytes = WebViewCacheHelper.GetTotalCacheSizeBytes(),
                cacheTotalDisplay = WebViewCacheHelper.FormatSize(WebViewCacheHelper.GetTotalCacheSizeBytes()),
                cacheLimitGb = service.Settings.WebViewCacheLimitGB,
                cacheLimitBytes = WebViewCacheHelper.GbToBytes(service.Settings.WebViewCacheLimitGB)
            });
        });

        // API: server toggle（仅管理端/管理员可操作）
        app.MapPost("/api/server/toggle", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            _fileShareEnabled = !_fileShareEnabled;
            var status = _fileShareEnabled ? "已启动" : "已停止";
            service.AddLog(_fileShareEnabled ? "INFO" : "WARN", "服务端", $"文件共享{status}");
            return Results.Json(new { running = _fileShareEnabled });
        });

        app.MapGet("/api/server/state", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(new { running = _fileShareEnabled });
        });

        // API: network interfaces
        app.MapGet("/api/network/interfaces", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(service.GetNetworkInterfaces());
        });

        // API: settings（仅管理端/管理员可操作）
        app.MapGet("/api/settings", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(service.Settings);
        });
        app.MapPost("/api/settings", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var bodyJson = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(bodyJson);
            var root = doc.RootElement;

            var current = service.Settings;
            var publicShareChanged = false;
            if (root.TryGetProperty("uploadDirectory", out var ud) && ud.ValueKind == JsonValueKind.String)
                current.UploadDirectory = ud.GetString() ?? current.UploadDirectory;
            // 端口仅由 /api/settings/port 管理，此处不再处理
            if (root.TryGetProperty("autoStart", out var astrt) && (astrt.ValueKind == JsonValueKind.True || astrt.ValueKind == JsonValueKind.False))
                current.AutoStart = astrt.GetBoolean();
            if (root.TryGetProperty("minimizeToTray", out var mtt) && (mtt.ValueKind == JsonValueKind.True || mtt.ValueKind == JsonValueKind.False))
                current.MinimizeToTray = mtt.GetBoolean();
            if (root.TryGetProperty("selectedInterface", out var si) && si.ValueKind == JsonValueKind.String)
                current.SelectedInterface = si.GetString() ?? "";
            if (root.TryGetProperty("publicShareDirectory", out var psd) && psd.ValueKind == JsonValueKind.String)
            {
                var val = psd.GetString() ?? "";
                current.PublicShareDirectories = string.IsNullOrWhiteSpace(val)
                    ? new List<string>()
                    : current.PublicShareDirectories.Append(val).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                publicShareChanged = true;
            }
            if (root.TryGetProperty("publicShareDirectories", out var psdArr) && psdArr.ValueKind == JsonValueKind.Array)
            {
                current.PublicShareDirectories = psdArr.EnumerateArray()
                    .Where(d => d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                    .Select(d => d.GetString()!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                publicShareChanged = true;
            }
            if (root.TryGetProperty("publicShareEnabled", out var pse) && (pse.ValueKind == JsonValueKind.True || pse.ValueKind == JsonValueKind.False))
            {
                current.PublicShareEnabled = pse.GetBoolean();
                publicShareChanged = true;
            }
            if (root.TryGetProperty("publicShareReadOnly", out var psro) && (psro.ValueKind == JsonValueKind.True || psro.ValueKind == JsonValueKind.False))
            {
                current.PublicShareReadOnly = psro.GetBoolean();
            }
            if (root.TryGetProperty("webViewCacheLimitGB", out var wcl) && wcl.ValueKind == JsonValueKind.Number)
                current.WebViewCacheLimitGB = Math.Max(0.1, wcl.GetDouble());

            service.SaveSettings(current);
            if (publicShareChanged)
            {
                service.EnsurePublicShareDirectories();
                // 公共共享目录变更后预构建索引
                if (current.PublicShareEnabled)
                {
                    foreach (var dir in service.GetPublicSharePaths())
                        _ = service.BuildIndexAsync(dir);
                }
            }
            _mainForm?.ApplyMinimizeToTray(current.MinimizeToTray);
            return Results.Json(new { success = true });
        });

        app.MapPost("/api/settings/port", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("port", out var portEl)
                || !portEl.TryGetInt32(out var port)
                || port is < 1 or > 65535)
            {
                return Results.Json(new { success = false, error = "无效端口号" }, statusCode: 400);
            }

            var current = service.Settings;
            var previousPort = current.Port;
            current.Port = port;
            service.SaveSettings(current);
            service.AddLog("INFO", "服务端", $"监听端口已设为 {port}，重启后生效");
            return Results.Json(new
            {
                success = true,
                port,
                previousPort,
                activePort = _activeListenPort,
                requiresRestart = port != _activeListenPort
            });
        });

        // API: bandwidth management（管理端 + 管理员用户可访问）
        app.MapGet("/api/bandwidth/users", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var onlineUsers = authService.GetOnlineUsers();
            return Results.Json(service.GetBandwidthUsers(onlineUsers));
        });

        app.MapPost("/api/bandwidth/ban", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(ip))
                return Results.Json(new { success = false, error = "缺少 IP 地址" }, statusCode: 400);
            service.BanIP(ip);
            return Results.Json(new { success = true });
        });

        app.MapPost("/api/bandwidth/unban", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(ip))
                return Results.Json(new { success = false, error = "缺少 IP 地址" }, statusCode: 400);
            service.UnbanIP(ip);
            return Results.Json(new { success = true });
        });

        app.MapPost("/api/bandwidth/limit", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var userId = root.TryGetProperty("userId", out var uid) ? uid.GetString() ?? "" : "";
            var upLimit = root.TryGetProperty("upLimit", out var up) && up.TryGetDouble(out var u) ? u : 0;
            var downLimit = root.TryGetProperty("downLimit", out var down) && down.TryGetDouble(out var d) ? d : 0;
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Json(new { success = false, error = "缺少 userId" }, statusCode: 400);
            service.SetBandwidthLimit(userId, Math.Max(0, upLimit), Math.Max(0, downLimit));
            return Results.Json(new { success = true });
        });

        app.MapGet("/api/bandwidth/blacklist", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(service.GetBlacklistedIPs());
        });
        app.MapGet("/api/cache/info", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var totalBytes = WebViewCacheHelper.GetTotalCacheSizeBytes();
            var limitGb = service.Settings.WebViewCacheLimitGB;
            return Results.Json(new
            {
                totalBytes,
                totalDisplay = WebViewCacheHelper.FormatSize(totalBytes),
                limitGb,
                limitBytes = WebViewCacheHelper.GbToBytes(limitGb),
                exceeded = totalBytes > WebViewCacheHelper.GbToBytes(limitGb)
            });
        });

        // API: cache clean
        app.MapPost("/api/cache/clean", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var sizeBefore = WebViewCacheHelper.GetTotalCacheSizeBytes();

            // 1. 清理不活跃的历史缓存目录
            WebViewCacheHelper.DeleteInactiveCacheDirectories(_mainForm?.ActiveCacheDir);

            // 2. 清理活跃缓存目录内的文件（不删目录本身，避免 WebView2 报错）
            var activeDir = _mainForm?.ActiveCacheDir;
            if (!string.IsNullOrEmpty(activeDir))
                WebViewCacheHelper.CleanDirectoryContents(activeDir);

            // 3. 清理废弃上传会话（手动清理时立即清理所有）
            var cleaned = WebViewCacheHelper.CleanupAbandonedUploadSessions(TimeSpan.Zero);

            var sizeAfter = WebViewCacheHelper.GetTotalCacheSizeBytes();
            service.AddLog("INFO", "服务端", $"清理缓存: {WebViewCacheHelper.FormatSize(sizeBefore)} → {WebViewCacheHelper.FormatSize(sizeAfter)}"
                + (cleaned > 0 ? $" (含废弃上传会话 {WebViewCacheHelper.FormatSize(cleaned)})" : ""));
            return Results.Json(new
            {
                success = true,
                sizeBefore,
                sizeAfter,
                sizeBeforeDisplay = WebViewCacheHelper.FormatSize(sizeBefore),
                sizeAfterDisplay = WebViewCacheHelper.FormatSize(sizeAfter)
            });
        });

        // API: 用户操作日志（仅管理端/管理员）
        app.MapPost("/api/log/action", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

            if (!string.IsNullOrEmpty(action))
            {
                var username = "管理端";
                if (ctx.Request.Cookies.TryGetValue("lfs_user", out var uname) && !string.IsNullOrEmpty(uname))
                    username = Uri.UnescapeDataString(uname);
                var msg = string.IsNullOrEmpty(detail) ? action : $"{action}（{detail}）";
                service.AddLog("OP", username, msg);
            }
            return Results.Json(new { success = true });
        });

        app.MapPost("/api/app/restart", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            if (!RestartApplication())
                return Results.Json(new { success = false, error = "无法定位程序路径" });
            return Results.Json(new { success = true });
        });

        // API: logs（仅管理端/管理员）
        app.MapGet("/api/logs", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(service.GetRecentLogs(100));
        });
        app.MapDelete("/api/logs", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            service.ClearLogs();
            return Results.Json(new { success = true });
        });
        app.MapPost("/api/logs/open-folder", () =>
        {
            try
            {
                var logDir = Path.Combine(AppPaths.DataDirectory, "log");
                Directory.CreateDirectory(logDir);
                if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"\"{logDir}\"");
                else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", logDir);
                else System.Diagnostics.Process.Start("xdg-open", logDir);
                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                _service?.AddLog("ERROR", "服务端", $"打开日志目录失败: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        app.MapPost("/api/bug/submit", () =>
        {
            try
            {
                var bugUploaderPath = Path.Combine(AppPaths.DataDirectory, "bugupload", "BugUploader.exe");
                if (!File.Exists(bugUploaderPath))
                    return Results.Json(new { success = false, error = $"BugUploader.exe 未找到: {bugUploaderPath}" });
                System.Diagnostics.Process.Start(bugUploaderPath);
                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                _service?.AddLog("ERROR", "服务端", $"启动Bug上传工具失败: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        app.MapGet("/api/logs/stream", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) { ctx.Response.StatusCode = 403; return; }
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("Connection", "keep-alive");
            var sw = new StreamWriter(ctx.Response.Body);
            sseClients.Add(sw);
            var recentLogs = service.GetRecentLogs(50);
            foreach (var log in recentLogs.Reverse<LogEntry>())
            {
                var jl = JsonSerializer.Serialize(log, jsonOpts);
                await sw.WriteAsync($"data: {jl}\n\n");
            }
            await sw.FlushAsync();
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(30000, ctx.RequestAborted);
                    await sw.WriteAsync(": keepalive\n\n");
                    await sw.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            finally { sseClients.TryTake(out _); }
        });

        // API: QR code（仅管理端/管理员）
        app.MapGet("/api/qrcode", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) { ctx.Response.StatusCode = 403; ctx.Response.ContentType = "text/plain; charset=utf-8"; return ctx.Response.WriteAsync("Forbidden"); }
            var primaryIP = service.GetPrimaryIP();
            var url = service.GetClientUrl(primaryIP, service.Settings.Port);
            try
            {
                using var qrGenerator = new QRCodeGenerator();
                using var qrData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                using var pngQr = new PngByteQRCode(qrData);
                var bytes = pngQr.GetGraphic(10);
                ctx.Response.ContentType = "image/png";
                return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", $"QR生成失败: {ex.Message}");
                ctx.Response.StatusCode = 500;
                return ctx.Response.WriteAsync("QR Code generation failed");
            }
        });

        // API: auth (客户端登录)
        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp))
                return Results.Json(new { success = false, error = "您的 IP 已被封禁" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var username = data?.GetValueOrDefault("username", "") ?? "";
            var password = data?.GetValueOrDefault("password", "") ?? "";
            var (ok, err, session) = authService.Login(username, password);
            if (!ok || session == null)
                return Results.Json(new { success = false, error = err ?? "登录失败" }, statusCode: 401);
            authService.SetSessionCookie(ctx, session);
            authService.OnLoginSuccess(session, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            service.AddLog("CONN", "客户端", $"登录 {session.Username}");
            var loginUser = userService.FindById(session.UserId);
            return Results.Json(new { success = true, username = session.Username, folder = session.Folder, folders = session.Folders, isAdmin = loginUser?.IsAdmin ?? false });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            authService.Logout(ctx);
            if (session != null)
                service.AddLog("INFO", "客户端", $"退出 {session.Username}");
            return Results.Json(new { success = true });
        });

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { authenticated = false });
            var user = userService.FindById(session.UserId);
            return Results.Json(new { authenticated = true, userId = session.UserId, username = session.Username, folder = session.Folder, folders = session.Folders, readOnly = session.ReadOnly, isAdmin = user?.IsAdmin ?? false });
        });

        app.MapPost("/api/auth/change-password", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "未登录" }, statusCode: 401);

            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var oldPassword = data?.GetValueOrDefault("oldPassword", "") ?? "";
            var newPassword = data?.GetValueOrDefault("newPassword", "") ?? "";
            var (ok, err) = userService.ChangePassword(session.UserId, oldPassword, newPassword);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            return Results.Json(new { success = true });
        });

        // API: users (服务端管理，仅管理端/管理员可操作)
        app.MapGet("/api/users", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(userService.ListUsers());
        });

        // 用户列表+在线状态（普通用户也可查看）
        app.MapGet("/api/users/status", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { error = "请先登录" }, statusCode: 401);
            var allUsers = userService.ListUsers();
            var onlineSet = authService.GetOnlineUsers()
                .ToDictionary(u => u.Username, u => u.Ip, StringComparer.OrdinalIgnoreCase);
            var result = allUsers.Select(u => new
            {
                username = u.Username,
                ip = onlineSet.TryGetValue(u.Username, out var ip) ? ip : "",
                online = onlineSet.ContainsKey(u.Username)
            })
            .OrderByDescending(u => u.online)
            .ThenBy(u => u.username, StringComparer.OrdinalIgnoreCase)
            .ToList();
            return Results.Json(new { success = true, users = result });
        });

        app.MapPost("/api/users", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null) return Results.Json(new { success = false, error = "无效请求" }, statusCode: 400);
            var username = req.GetValueOrDefault("username").GetString() ?? "";
            var password = req.GetValueOrDefault("password").GetString() ?? "";
            // 支持 folders 数组（优先）或 folder 字符串（向后兼容）
            var folders = new List<string>();
            if (req.TryGetValue("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in foldersEl.EnumerateArray())
                {
                    var f = item.GetString();
                    if (!string.IsNullOrWhiteSpace(f)) folders.Add(f);
                }
            }
            if (folders.Count == 0 && req.TryGetValue("folder", out var folderEl))
            {
                var folder = folderEl.GetString();
                if (!string.IsNullOrWhiteSpace(folder)) folders.Add(folder);
            }
            var enabled = !req.ContainsKey("enabled") || req["enabled"].GetBoolean();
            var readOnly = req.TryGetValue("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True;
            var isAdmin = req.TryGetValue("isAdmin", out var ia) && ia.ValueKind == JsonValueKind.True;
            var (ok, err, user) = userService.CreateUser(username, password, folders, enabled, readOnly, isAdmin);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            // 为新用户的目录预构建索引
            foreach (var f in folders)
            {
                if (Directory.Exists(f))
                    _ = service.BuildIndexAsync(Path.GetFullPath(f));
            }
            return Results.Json(new { success = true, user });
        });

        app.MapPut("/api/users/{id}", async (string id, HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null) return Results.Json(new { success = false, error = "无效请求" }, statusCode: 400);
            var password = req.TryGetValue("password", out var pwEl) && pwEl.ValueKind == JsonValueKind.String
                ? pwEl.GetString()
                : null;
            if (string.IsNullOrEmpty(password)) password = null;
            // 支持 folders 数组（优先）或 folder 字符串（向后兼容）
            var folders = new List<string>();
            if (req.TryGetValue("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in foldersEl.EnumerateArray())
                {
                    var f = item.GetString();
                    if (!string.IsNullOrWhiteSpace(f)) folders.Add(f);
                }
            }
            if (folders.Count == 0 && req.TryGetValue("folder", out var folderEl))
            {
                var folder = folderEl.GetString();
                if (!string.IsNullOrWhiteSpace(folder)) folders.Add(folder);
            }
            var enabled = !req.ContainsKey("enabled") || req["enabled"].GetBoolean();
            var readOnly = req.TryGetValue("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True;
            var isAdmin = req.TryGetValue("isAdmin", out var ia) ? ia.ValueKind == JsonValueKind.True ? (bool?)true : (ia.ValueKind == JsonValueKind.False ? (bool?)false : null) : null;
            var (ok, err) = userService.UpdateUser(id, password, folders, enabled, readOnly, isAdmin);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            // 用户目录变更后为新目录预构建索引
            foreach (var f in folders)
            {
                if (Directory.Exists(f))
                    _ = service.BuildIndexAsync(Path.GetFullPath(f));
            }
            return Results.Json(new { success = true });
        });

        app.MapDelete("/api/users/{id}", (HttpContext ctx, string id) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var (ok, err) = userService.DeleteUser(id);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 404);
            return Results.Json(new { success = true });
        });

        // 用户自助注册（无需登录）
        app.MapPost("/api/auth/register", async (HttpContext ctx) =>
        {
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp))
                return Results.Json(new { success = false, error = "您的 IP 已被封禁" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var username = data?.GetValueOrDefault("username", "") ?? "";
            var password = data?.GetValueOrDefault("password", "") ?? "";
            var confirmPassword = data?.GetValueOrDefault("confirmPassword", "") ?? "";
            var (ok, err) = userService.Register(username, password, confirmPassword);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            return Results.Json(new { success = true, message = "注册成功，请等待管理员审批" });
        });

        // 获取待审批用户数量（管理端）
        app.MapGet("/api/users/pending-count", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            return Results.Json(new { count = userService.GetPendingCount() });
        });

        // 审批通过注册用户（管理端）
        app.MapPost("/api/users/{id}/approve", async (string id, HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // 支持 folders 数组（优先）或 folder 字符串（向后兼容）
            var folders = new List<string>();
            if (data != null && data.TryGetValue("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in foldersEl.EnumerateArray())
                {
                    var f = item.GetString();
                    if (!string.IsNullOrWhiteSpace(f)) folders.Add(f);
                }
            }
            if (folders.Count == 0 && data != null && data.TryGetValue("folder", out var fEl))
            {
                var folder = fEl.GetString();
                if (!string.IsNullOrWhiteSpace(folder)) folders.Add(folder);
            }
            var (ok, err, user) = userService.ApproveUser(id, folders);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            foreach (var f in folders)
            {
                if (Directory.Exists(f))
                    _ = service.BuildIndexAsync(Path.GetFullPath(f));
            }
            return Results.Json(new { success = true, user });
        });

        // 拒绝注册申请（管理端）
        app.MapPost("/api/users/{id}/reject", (string id, HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var (ok, err) = userService.RejectUser(id);
            if (!ok) return Results.Json(new { success = false, error = err }, statusCode: 400);
            return Results.Json(new { success = true });
        });

        app.MapGet("/api/users/template", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var templatePath = Path.Combine(AppPaths.ResourceDirectory, "用户导入模板.xlsx");
            if (!File.Exists(templatePath)) return Results.Json(new { success = false, error = "模板文件不存在" }, statusCode: 404);
            var bytes = File.ReadAllBytes(templatePath);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "用户导入模板.xlsx");
        });

        app.MapPost("/api/users/import", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || !req.TryGetValue("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
                return Results.Json(new { success = false, error = "无效请求" }, statusCode: 400);

            string? defaultFolder = null;
            if (req.TryGetValue("defaultFolder", out var folderEl) && folderEl.ValueKind == JsonValueKind.String)
                defaultFolder = folderEl.GetString();

            var entries = new List<ImportUserEntry>();
            foreach (var item in usersEl.EnumerateArray())
            {
                entries.Add(new ImportUserEntry
                {
                    Username = item.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                    Password = item.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "",
                    Folder = item.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "",
                    Enabled = !item.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False,
                    ReadOnly = item.TryGetProperty("readOnly", out var r) && r.ValueKind == JsonValueKind.True
                });
            }

            if (entries.Count == 0)
                return Results.Json(new { success = false, error = "导入列表为空" }, statusCode: 400);

            var result = userService.ImportUsers(entries, defaultFolder);
            // 为导入用户的目录预构建索引（去重）
            foreach (var f in entries
                .Select(e => string.IsNullOrEmpty(e.Folder) ? string.IsNullOrEmpty(defaultFolder) ? "" : defaultFolder : e.Folder)
                .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f))
                .Select(f => Path.GetFullPath(f!))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = service.BuildIndexAsync(f);
            }
            return Results.Json(new
            {
                success = result.FailedCount == 0,
                successCount = result.SuccessCount,
                failedCount = result.FailedCount,
                errors = result.Errors
            });
        });

        // API: 分享 - 获取可分享目标用户列表及当前用户的文件夹列表
        app.MapGet("/api/share/target-users", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);

            // 获取当前用户已经分享过的目标用户 ID（排除这些用户，避免混淆）
            var outgoingShares = userService.GetOutgoingShares(session.UserId);
            var alreadySharedUserIds = outgoingShares.Select(s => s.TargetUserId).Distinct().ToHashSet();

            // 获取除当前用户外的所有其他用户，排除已共享过的
            var allUsers = userService.ListUsers()
                .Where(u => u.Id != session.UserId && !alreadySharedUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.Folder, u.ReadOnly, u.Enabled })
                .ToList();

            // 获取当前用户可访问的文件夹列表（仅子目录，不包含根目录）
            var rootFolder = session.Folders.Count > 0 ? session.Folders[0] : session.Folder;
            var folders = new List<object>();
            if (Directory.Exists(rootFolder))
            {
                // 只添加直接子目录，不包含根目录本身
                try
                {
                    foreach (var dir in Directory.GetDirectories(rootFolder))
                    {
                        var dirName = Path.GetFileName(dir);
                        folders.Add(new { path = dirName, name = dirName, fullPath = dir });
                    }
                }
                catch { }
            }

            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "-1";
            return Results.Json(new
            {
                success = true,
                currentUserReadOnly = session.ReadOnly,
                users = allUsers,
                folders = folders,
                alreadySharedCount = alreadySharedUserIds.Count
            });
        });

        // API: 分享 - 浏览目录内容（用于分享时的文件/文件夹选择器）
        app.MapGet("/api/share/browse", (HttpContext ctx, string? path) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            if (session.ReadOnly)
                return Results.Json(new { success = false, error = "只读账户不可分享" }, statusCode: 403);

            try
            {
                var rootFolder = session.Folders.Count > 0 ? session.Folders[0] : session.Folder;
                if (string.IsNullOrEmpty(rootFolder))
                    return Results.Json(new { success = false, error = "用户目录未配置" }, statusCode: 500);
                var browsePath = string.IsNullOrEmpty(path)
                    ? rootFolder
                    : Path.GetFullPath(Path.Combine(rootFolder, path));

                // 安全检查：确保浏览路径在根目录内
                var normalizedRoot = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!browsePath.StartsWith(normalizedRoot) && browsePath != Path.GetFullPath(rootFolder))
                    return Results.Json(new { success = false, error = "路径越权" }, statusCode: 403);

                if (!Directory.Exists(browsePath))
                    return Results.Json(new { success = false, error = "目录不存在" }, statusCode: 404);

                // 计算相对路径用于前端导航（统一使用 / 分隔，避免 Windows \ 泄漏到前端 JS 字符串中被误转义）
                var relativePath = string.IsNullOrEmpty(path) ? "" :
                    browsePath.Substring(normalizedRoot.Length).TrimEnd(Path.DirectorySeparatorChar).Replace('\\', '/');

                // 分别收集目录和文件，避免匿名类型差异导致排序异常
                var dirItems = new List<object>();
                var fileItems = new List<object>();

                // 添加子目录
                try
                {
                    foreach (var dir in Directory.GetDirectories(browsePath))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if ((dirInfo.Attributes & FileAttributes.Hidden) != 0) continue;
                        var dirName = Path.GetFileName(dir);
                        var relPath = string.IsNullOrEmpty(relativePath) ? dirName : $"{relativePath}/{dirName}";
                        dirItems.Add(new
                        {
                            name = dirName,
                            path = relPath,
                            type = "directory",
                            hasChildren = Directory.GetDirectories(dir).Length > 0 || Directory.GetFiles(dir).Length > 0
                        });
                    }
                }
                catch { }

                // 按名称排序目录
                dirItems.Sort((a, b) =>
                {
                    var na = (string)((dynamic)a).name;
                    var nb = (string)((dynamic)b).name;
                    return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
                });

                // 添加文件
                try
                {
                    foreach (var file in Directory.GetFiles(browsePath))
                    {
                        var fileInfo = new FileInfo(file);
                        if ((fileInfo.Attributes & FileAttributes.Hidden) != 0) continue;
                        var fileName = Path.GetFileName(file);
                        var relPath = string.IsNullOrEmpty(relativePath) ? fileName : $"{relativePath}/{fileName}";
                        fileItems.Add(new
                        {
                            name = fileName,
                            path = relPath,
                            type = "file",
                            size = fileInfo.Length,
                            sizeDisplay = FormatSize(fileInfo.Length),
                            lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                        });
                    }
                }
                catch { }

                // 按名称排序文件
                fileItems.Sort((a, b) =>
                {
                    var na = (string)((dynamic)a).name;
                    var nb = (string)((dynamic)b).name;
                    return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
                });

                // 合并：目录在前，文件在后
                var items = new List<object>();
                items.AddRange(dirItems);
                items.AddRange(fileItems);

                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                return Results.Json(new
                {
                    success = true,
                    currentPath = relativePath,
                    isRoot = string.IsNullOrEmpty(relativePath),
                    items
                });
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "API", $"浏览分享目录失败: {ex.Message}");
                return Results.Json(new { success = false, error = $"浏览目录时发生错误: {ex.Message}" }, statusCode: 500);
            }
        });

        // API: 分享 - 将文件或文件夹权限授予其他用户（不复制文件）
        app.MapPost("/api/share/apply", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            if (session.ReadOnly)
                return Results.Json(new { success = false, error = "只读账户不可分享" }, statusCode: 403);

            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null)
                return Results.Json(new { success = false, error = "无效请求" }, statusCode: 400);

            var targetUserIds = new List<string>();
            if (req.TryGetValue("targetUserIds", out var userIdsEl) && userIdsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in userIdsEl.EnumerateArray())
                {
                    var id = item.GetString();
                    if (!string.IsNullOrEmpty(id))
                        targetUserIds.Add(id);
                }
            }
            if (targetUserIds.Count == 0)
                return Results.Json(new { success = false, error = "请选择目标用户" }, statusCode: 400);

            var folderPaths = new List<string>();
            if (req.TryGetValue("folderPaths", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in foldersEl.EnumerateArray())
                {
                    var p = item.GetString();
                    if (!string.IsNullOrEmpty(p))
                        folderPaths.Add(p);
                }
            }
            if (folderPaths.Count == 0)
                return Results.Json(new { success = false, error = "请选择要共享的文件或文件夹" }, statusCode: 400);

            var writePermission = false;
            if (req.TryGetValue("writePermission", out var wpEl) && wpEl.ValueKind == JsonValueKind.True)
                writePermission = true;
            if (session.ReadOnly)
                writePermission = false;

            var shareRoot = session.Folders.Count > 0 ? session.Folders[0] : session.Folder;
            var sharedCount = 0;
            var errors = new List<string>();

            foreach (var targetId in targetUserIds)
            {
                foreach (var itemRelPath in folderPaths)
                {
                    var sourceFullPath = string.IsNullOrEmpty(itemRelPath)
                        ? shareRoot
                        : Path.GetFullPath(Path.Combine(shareRoot, itemRelPath));

                    var normalizedRoot = Path.GetFullPath(shareRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!sourceFullPath.StartsWith(normalizedRoot) &&
                        sourceFullPath != Path.GetFullPath(shareRoot))
                    {
                        errors.Add($"路径越权: {itemRelPath}");
                        continue;
                    }

                    var isFile = File.Exists(sourceFullPath);
                    var isDir = Directory.Exists(sourceFullPath);
                    if (!isFile && !isDir)
                    {
                        errors.Add($"文件/文件夹不存在: {itemRelPath}");
                        continue;
                    }

                    var itemName = Path.GetFileName(sourceFullPath);
                    var displayName = string.IsNullOrEmpty(itemRelPath)
                        ? $"{session.Username} 的 {Path.GetFileName(shareRoot)}"
                        : $"{session.Username} 的 {itemName}";
                    // 标记是文件还是目录，便于前端区分展示
                    var displayPath = string.IsNullOrEmpty(itemRelPath)
                        ? $"{session.Username} 的 {Path.GetFileName(shareRoot)}"
                        : $"{session.Username} 的 {itemRelPath}";

                    var (ok, err, _) = userService.AddSharedFolder(
                        targetId, session.UserId, session.Username,
                        sourceFullPath, displayName, writePermission, isFile);

                    if (ok)
                    {
                        sharedCount++;
                        // 为共享源目录预构建索引，加速接收方搜索
                        if (!isFile && Directory.Exists(sourceFullPath))
                            _ = service.BuildIndexAsync(sourceFullPath);
                        var targetUser = userService.FindById(targetId);
                        var targetName = targetUser?.Username ?? targetId;
                        var itemType = isFile ? "文件" : "文件夹";
                        service.AddLog("INFO", "客户端", $"用户 {session.Username} 将{itemType} {itemRelPath} 共享给 {targetName}（{(writePermission ? "读写" : "只读")}）");
                    }
                    else
                    {
                        errors.Add(err ?? "分享失败");
                    }
                }
            }

            return Results.Json(new
            {
                success = errors.Count == 0,
                sharedCount,
                errorCount = errors.Count,
                errors = errors.Take(5).ToList()
            });
        });

        // API: 分享 - 获取当前用户被分享的文件夹列表（接收到的）
        app.MapGet("/api/share/list", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);

            var received = userService.GetSharedFolders(session.UserId)
                .Select(sf => new
                {
                    sf.Id,
                    sf.SourceUserId,
                    sf.SourceUsername,
                    sf.SourcePath,
                    sf.DisplayName,
                    sf.WritePermission,
                    sf.IsFile,
                    SharedAt = sf.SharedAt.ToString("yyyy-MM-dd HH:mm")
                }).ToList();

            return Results.Json(new { success = true, received });
        });

        // API: 分享 - 获取当前用户分享出去的记录（发出的）
        app.MapGet("/api/share/outgoing", (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);

            var outgoing = userService.GetOutgoingShares(session.UserId)
                .Select(item => new
                {
                    item.Entry.Id,
                    item.Entry.SourceUserId,
                    item.Entry.SourceUsername,
                    item.Entry.SourcePath,
                    item.Entry.DisplayName,
                    item.Entry.WritePermission,
                    item.Entry.IsFile,
                    item.Entry.SharedAt,
                    TargetUsername = item.TargetUsername,
                    TargetUserId = item.TargetUserId,
                    SharedAtDisplay = item.Entry.SharedAt.ToString("yyyy-MM-dd HH:mm")
                }).ToList();

            return Results.Json(new { success = true, outgoing });
        });

        // API: 分享 - 取消分享（发出者撤回 / 接收者移除）
        app.MapPost("/api/share/cancel", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);

            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null)
                return Results.Json(new { success = false, error = "无效请求" }, statusCode: 400);

            var shareId = req.GetValueOrDefault("shareId").GetString() ?? "";
            var targetUserId = req.GetValueOrDefault("targetUserId").GetString() ?? "";
            if (string.IsNullOrEmpty(shareId) || string.IsNullOrEmpty(targetUserId))
                return Results.Json(new { success = false, error = "参数不完整" }, statusCode: 400);

            // 安全检查：只有分享的发出者或接收者可以取消
            var targetUser = userService.FindById(targetUserId);
            if (targetUser == null)
                return Results.Json(new { success = false, error = "目标用户不存在" }, statusCode: 404);

            var shareEntry = targetUser.SharedFolders.FirstOrDefault(sf => sf.Id == shareId);
            if (shareEntry == null)
                return Results.Json(new { success = false, error = "共享记录不存在" }, statusCode: 404);

            // 发出者或接收者都可以取消
            if (shareEntry.SourceUserId != session.UserId && targetUserId != session.UserId)
                return Results.Json(new { success = false, error = "无权取消此共享" }, statusCode: 403);

            var (ok, err) = userService.RemoveSharedFolder(targetUserId, shareId);
            if (!ok)
                return Results.Json(new { success = false, error = err ?? "取消失败" }, statusCode: 400);

            service.AddLog("INFO", "客户端", $"用户 {session.Username} 取消了共享 {shareEntry.DisplayName} → {targetUser.Username}");
            return Results.Json(new { success = true });
        });

        // API: file list
        app.MapGet("/api/files", (HttpContext ctx, string? path) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            ctx.Response.Headers.CacheControl = "no-store";

            try
            {
                // 检查是否是共享文件夹路径（格式: __shared__/shareId/subpath）
                if (!string.IsNullOrEmpty(path) && path.StartsWith("__shared__/"))
                {
                    var parts = path.Substring("__shared__/".Length).Split('/', 2);
                    var shareId = parts[0];
                    var subPath = parts.Length > 1 ? parts[1] : "";
                    var sharedEntry = userService.GetSharedFolders(session.UserId)
                        .FirstOrDefault(sf => sf.Id == shareId);
                    if (sharedEntry == null)
                        return Results.Json(new { error = "共享内容不存在" }, statusCode: 404);
                    // 如果是文件，不允许列出目录
                    if (sharedEntry.IsFile)
                        return Results.Json(new { error = "共享内容为文件，无法浏览" }, statusCode: 400);
                    // 检查源目录是否存在
                    if (!Directory.Exists(sharedEntry.SourcePath))
                        return Results.Json(new { error = "共享源目录不存在，请联系分享者" }, statusCode: 404);
                    return Results.Json(service.ListFiles(subPath, sharedEntry.SourcePath));
                }

                // 多目录共享：根目录时，把所有共享根都作为虚拟目录条目返回（与 __shared__/ 行为一致）
                if (string.IsNullOrEmpty(path))
                {
                    var rootEntries = new List<FileItem>();
                    var shared = userService.GetSharedFolders(session.UserId)
                        .Where(sf => sf.IsFile ? File.Exists(sf.SourcePath) : Directory.Exists(sf.SourcePath))
                        .Select(sf => new FileItem
                        {
                            Name = sf.DisplayName,
                            Path = "__shared__/" + sf.Id,
                            IsDirectory = !sf.IsFile,
                            IsShared = true,
                            Size = sf.IsFile ? (File.Exists(sf.SourcePath) ? new FileInfo(sf.SourcePath).Length : -1) : -1,
                            LastModified = sf.SharedAt
                        }).ToList();

                    // 自身共享根条目（多目录共享）
                    for (int i = 0; i < session.Folders.Count; i++)
                    {
                        var folderPath = session.Folders[i];
                        if (!Directory.Exists(folderPath)) continue;
                        var dirInfo = new DirectoryInfo(folderPath);
                        rootEntries.Add(new FileItem
                        {
                            Name = dirInfo.Name,
                            Path = "__folder__/" + i,
                            IsDirectory = true,
                            IsShared = false,
                            IsOwnRoot = true,
                            Size = -1,
                            LastModified = dirInfo.LastWriteTime
                        });
                    }
                    rootEntries.AddRange(shared);
                    return Results.Json(rootEntries);
                }

                // 子路径若以 __folder__/N 开头，使用对应共享根作为基址
                if (!string.IsNullOrEmpty(path) && path.StartsWith("__folder__/"))
                {
                    if (!TryResolveOwnFolderPath(path, session, out var rootBase, out var rootSub))
                        return Results.Json(new { error = "无效路径" }, statusCode: 400);
                    if (!Directory.Exists(rootBase))
                        return Results.Json(new { error = "共享根目录不存在" }, statusCode: 404);
                    // 共享根内部不递归显示其他共享根
                    return Results.Json(service.ListFiles(rootSub, rootBase));
                }

                {
                    var files = service.ListFiles(path ?? "", session.Folders.Count > 0 ? session.Folders[0] : session.Folder);
                    // 根目录时，共享文件夹置顶显示
                    if (string.IsNullOrEmpty(path))
                    {
                        var shared = userService.GetSharedFolders(session.UserId)
                            .Where(sf => sf.IsFile ? File.Exists(sf.SourcePath) : Directory.Exists(sf.SourcePath))
                            .Select(sf => new FileItem
                            {
                                Name = sf.DisplayName,
                                Path = sf.IsFile ? ("__shared__/" + sf.Id) : ("__shared__/" + sf.Id),
                                IsDirectory = !sf.IsFile,
                                IsShared = true,
                                Size = sf.IsFile ? (File.Exists(sf.SourcePath) ? new FileInfo(sf.SourcePath).Length : -1) : -1,
                                LastModified = sf.SharedAt
                            }).ToList();
                        files.InsertRange(0, shared);
                    }
                    return Results.Json(files);
                }
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "API", $"列出文件失败: {ex.Message}");
                return Results.Json(new { error = "加载文件列表失败" }, statusCode: 500);
            }
        });

        // API: file search (递归搜索自身目录 + 所有共享目录)
        app.MapGet("/api/files/search", (HttpContext ctx, string q) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            var session = authService.GetSession(ctx);
            if (session == null)
                return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            ctx.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(q))
                return Results.Json(new List<FileItem>());

            var results = new List<FileItem>();
            var maxResults = 200;

            // 1. 搜索自己的所有共享文件夹
            var foldersToSearch = (session.Folders != null && session.Folders.Count > 0)
                ? session.Folders
                : (string.IsNullOrEmpty(session.Folder) ? new List<string>() : new List<string> { session.Folder });
            foreach (var f in foldersToSearch)
            {
                if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                var rem = maxResults - results.Count;
                if (rem <= 0) break;
                var ownResults = service.SearchFilesWithIndex(q, f, rem);
                // 标注每个搜索结果来自哪个共享根，便于前端展示
                var rootName = Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var item in ownResults)
                {
                    if (string.IsNullOrEmpty(item.SharedSourceName))
                        item.SharedSourceName = "我的目录 / " + rootName;
                }
                results.AddRange(ownResults);
            }

            // 2. 搜索所有共享给我的文件夹
            var sharedFolders = userService.GetSharedFolders(session.UserId)
                .Where(sf => Directory.Exists(sf.SourcePath));
            foreach (var sf in sharedFolders)
            {
                var rem = maxResults - results.Count;
                if (rem <= 0) break;
                var sharedResults = service.SearchFilesWithIndex(q, sf.SourcePath, rem);
                foreach (var item in sharedResults)
                {
                    item.SharedSourceName = sf.DisplayName;
                    item.Path = $"__shared__/{sf.Id}/{item.Path}";
                }
                results.AddRange(sharedResults);
            }

            return Results.Json(results);
        });

        // API: file download (支持断点续传)
        app.MapGet("/api/files/download", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            service.AddLog("DOWN", "客户端", $"下载 {System.IO.Path.GetFileName(fullPath)}");
            await SendFileWithRangeAsync(ctx, fullPath, service, session.UserId, session.Username, clientIp);
        });

        // API: download metadata (HEAD 请求，获取文件大小和断点续传支持信息)
        app.MapMethods("/api/files/download", new[] { "HEAD" }, (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; return Task.CompletedTask; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
            var fileInfo = new FileInfo(fullPath);
            var fileName = System.IO.Path.GetFileName(fullPath);
            ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"");
            ctx.Response.Headers.Append("Accept-Ranges", "bytes");
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = fileInfo.Length;
            return Task.CompletedTask;
        });

        // API: file preview (图片/视频内联播放，支持 Range)
        app.MapGet("/api/files/preview", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            service.AddLog("VIEW", "客户端", $"预览 {Path.GetFileName(fullPath)}");
            await SendFilePreviewAsync(ctx, fullPath, service, session.UserId, session.Username, clientIp);
        });
        // API: file preview raw (返回文件原始内容，供前端 JS 库解析，不走 Range)
        app.MapGet("/api/files/preview/raw", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            var fileInfo = new FileInfo(fullPath);
            // 限制预览文件大小：最大 50MB
            if (fileInfo.Length > 50 * 1024 * 1024) { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync("文件过大，不支持预览"); return; }
            try
            {
                SetPreviewHeaders(ctx.Response, fullPath);
                ctx.Response.ContentLength = fileInfo.Length;
                // 使用 FileShare.Read 允许其他程序同时打开文件（如 Excel 正在编辑时仍可预览）
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(ctx.Response.Body);
                }
            }
            catch (IOException ex) when (IsFileLockedException(ex))
            {
                service.AddLog("WARN", "客户端", $"raw预览文件被占用 {Path.GetFileName(fullPath)}: {ex.Message}");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength = null;
                await ctx.Response.WriteAsync("文件被其他程序占用，请关闭文件后重试");
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "客户端", $"raw预览失败 {Path.GetFileName(fullPath)}: {ex.Message}");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength = null;
                await ctx.Response.WriteAsync("文件读取失败");
            }
        });
        app.MapMethods("/api/files/preview", new[] { "HEAD" }, (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; return Task.CompletedTask; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
            var fileInfo = new FileInfo(fullPath);
            var fileName = Path.GetFileName(fullPath);
            ctx.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{Uri.EscapeDataString(fileName)}\"");
            ctx.Response.Headers.Append("Accept-Ranges", "bytes");
            SetPreviewHeaders(ctx.Response, fullPath);
            ctx.Response.ContentLength = fileInfo.Length;
            return Task.CompletedTask;
        });

        // API: file upload
        app.MapPost("/api/files/upload", async (HttpContext ctx) =>
        {
            try
            {
                if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
                var session = authService.GetSession(ctx);
                if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
                if (!ctx.Request.HasFormContentType) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("请使用 multipart/form-data"); return; }
                var form = await ctx.Request.ReadFormAsync();
                var subPath = FileShareService.NormalizeSubPath(form["path"].FirstOrDefault() ?? "");
                var files = form.Files;
                if (files.Count == 0) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("未选择文件"); return; }

                // 检查写权限：共享文件夹需要 WritePermission，普通路径需要非 ReadOnly
                if (!HasWritePermission(subPath, session, userService))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync(subPath.StartsWith("__shared__/") ? "共享文件夹为只读，不可上传文件" : "只读账户不可上传文件");
                    return;
                }

                var (upLimitMBps, _) = service.GetBandwidthLimit(session.UserId);
                // 解析上传目标路径（同时兼容普通路径、__shared__/shareId、__folder__/index）
                string baseDir;
                string targetDir;
                if (string.IsNullOrEmpty(subPath)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("根目录禁止上传，请选择子目录"); return; }

                string physicalSub;
                var (resolvedBase, sharedBaseDir) = ResolveOwnDirBase(subPath, session, service, userService);
                if (resolvedBase == null) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("无效的上传路径"); return; }
                baseDir = resolvedBase;
                if (sharedBaseDir != null && !string.IsNullOrEmpty(subPath) && subPath.StartsWith("__shared__/"))
                {
                    var idx = subPath.IndexOf('/', "__shared__/".Length);
                    physicalSub = idx >= 0 ? subPath.Substring(idx + 1) : "";
                    targetDir = string.IsNullOrEmpty(physicalSub) ? baseDir : Path.Combine(baseDir, physicalSub.Replace('/', Path.DirectorySeparatorChar));
                }
                else if (!string.IsNullOrEmpty(subPath) && subPath.StartsWith("__folder__/"))
                {
                    var idx = subPath.IndexOf('/', "__folder__/".Length);
                    physicalSub = idx >= 0 ? subPath.Substring(idx + 1) : "";
                    targetDir = string.IsNullOrEmpty(physicalSub) ? baseDir : Path.Combine(baseDir, physicalSub.Replace('/', Path.DirectorySeparatorChar));
                }
                else
                {
                    targetDir = Path.Combine(baseDir, subPath.Replace('/', Path.DirectorySeparatorChar));
                }
                if (!FileShareService.IsPathUnderDirectory(baseDir, targetDir)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("无效的上传路径"); return; }
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                service.AddLog("INFO", "客户端", $"上传 {files.Count} 个文件 -> {targetDir}");

                var uploaded = new List<string>();
                foreach (var file in files)
                {
                    try
                    {
                        var relativePath = file.FileName.Replace('/', Path.DirectorySeparatorChar);
                        var safeRelativePath = SanitizePath(relativePath);
                        if (string.IsNullOrWhiteSpace(safeRelativePath))
                        {
                            service.AddLog("WARN", $"跳过无效文件名: {file.FileName}");
                            continue;
                        }
                        var destPath = Path.Combine(targetDir, safeRelativePath);
                        if (!FileShareService.IsPathUnderDirectory(baseDir, destPath))
                        {
                            service.AddLog("WARN", $"跳过越界路径: {safeRelativePath}");
                            continue;
                        }
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);
                        using var stream = new FileStream(destPath, FileMode.Create);
                        using var sourceStream = file.OpenReadStream();

                        // 初始化用户速度追踪器，然后通过进度回调实时更新
                        service.TrackUserUploaded(session.UserId, session.Username, clientIp, 0);
                        await CopyStreamWithThrottleAsync(sourceStream, stream, upLimitMBps, CancellationToken.None,
                            bytes => service.TrackUserUploadBytes(session.UserId, bytes));

                        service.AddLog("UPLD", "客户端", $"{safeRelativePath} ({FormatSize(file.Length)})");
                        uploaded.Add(safeRelativePath);
                    }
                    catch (Exception ex)
                    {
                        service.AddLog("ERROR", $"上传文件失败 [{file.FileName}]: {ex.Message}");
                    }
                }

                if (uploaded.Count == 0)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync("所有文件上传失败，请检查保存目录权限或文件名是否合法");
                    return;
                }

                service.AddLog("INFO", "客户端", $"上传完成 {uploaded.Count}/{files.Count}");
                await ctx.Response.WriteAsJsonAsync(new { success = true, files = uploaded });
            }
            catch (InvalidDataException ex)
            {
                service.AddLog("ERROR", $"上传请求解析失败: {ex.Message}");
                ctx.Response.StatusCode = 413;
                await ctx.Response.WriteAsync("上传数据过大，单个请求请不超过 200GB");
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", $"上传处理失败: {ex.Message}");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"上传失败: {ex.Message}");
            }
        });

        // API: chunked upload (分块上传，支持大文件断点续传)
        app.MapPost("/api/chunked/init", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var json = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var fileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
                var fileSize = root.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out var sz) ? sz : 0L;
                var subPath = root.TryGetProperty("subPath", out var sp) ? sp.GetString() ?? "" : "";
                var accessPath = subPath;
                var contentHash = root.TryGetProperty("contentHash", out var ch) ? ch.GetString() : null;

                // 非共享路径禁止在根目录上传
                if (!subPath.StartsWith("__shared__/") && string.IsNullOrEmpty(subPath))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("根目录禁止上传，请选择子目录");
                    return;
                }

                // 检查写权限
                if (!HasWritePermission(subPath, session, userService))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync(subPath.StartsWith("__shared__/") ? "共享文件夹为只读，不可上传文件" : "只读账户不可上传文件");
                    return;
                }

                // 解析共享路径：__shared__/shareId 与 __folder__/index 都解析为物理目录
                string? resolvedBaseDir = null;
                string resolvedSubPath = subPath;
                if (!string.IsNullOrEmpty(subPath) && subPath.StartsWith("__shared__/"))
                {
                    var (sharedPath, sharedBaseDir, _) = ResolveSharedPath(subPath, session.UserId, userService);
                    if (sharedPath != null && sharedBaseDir != null)
                    {
                        resolvedBaseDir = sharedBaseDir;
                        // 去掉 __shared__/shareId 前缀，只保留共享文件夹内的子路径
                        var parts = subPath.Substring("__shared__/".Length).Split('/', 2);
                        resolvedSubPath = parts.Length > 1 ? parts[1] : "";
                    }
                }
                else if (!string.IsNullOrEmpty(subPath) && subPath.StartsWith("__folder__/"))
                {
                    if (TryResolveOwnFolderPath(subPath, session, out var fRootBase, out var fSub))
                    {
                        resolvedBaseDir = fRootBase;
                        resolvedSubPath = fSub;
                    }
                }

                var (ok, error, uploadId, uploadedBytes, chunkSize, fileExists) = chunkedUploadService.Init(
                    session.UserId, session.Folders.Count > 0 ? session.Folders[0] : session.Folder, resolvedSubPath, fileName, fileSize, resolvedBaseDir, contentHash, accessPath);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "初始化失败");
                    return;
                }
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                service.TrackUserUploaded(session.UserId, session.Username, clientIp, 0);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, uploadId, uploadedBytes, chunkSize, fileExists });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"初始化失败: {ex.Message}");
            }
        });

        app.MapPost("/api/chunked/write", async (HttpContext ctx) =>
        {
            var bodyLimit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = UploadLimits.MaxChunkRequestSize;
            if (ctx.Request.ContentLength > UploadLimits.MaxChunkRequestSize)
            { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync("分块过大。请重新选择文件上传。"); return; }

            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            // 服务层按当前账户和共享配置重新验证写权限。
            try
            {
                var uploadId = ctx.Request.Query["uploadId"].ToString();
                if (!long.TryParse(ctx.Request.Query["offset"], out var offset)) offset = 0;
                if (string.IsNullOrEmpty(uploadId))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("缺少 uploadId");
                    return;
                }

                var (ok, error, uploadedBytes) = await chunkedUploadService.WriteChunkAsync(
                    session.UserId, uploadId, offset, ctx.Request.Body, ctx.RequestAborted);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "写入分块失败");
                    return;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, uploadedBytes });
            }
            catch (InvalidDataException ex)
            { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync(ex.Message); }
            catch (BadHttpRequestException ex)
            { ctx.Response.StatusCode = ex.StatusCode; }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"写入分块失败: {ex.Message}");
            }
        });

        app.MapPost("/api/chunked/complete", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            // 完成上传时也重新验证目标目录和写权限。
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var json = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var uploadId = root.TryGetProperty("uploadId", out var uid) ? uid.GetString() ?? "" : "";
                var overwrite = root.TryGetProperty("overwrite", out var ow) && ow.ValueKind == JsonValueKind.True;

                var (ok, error, relativePath) = chunkedUploadService.Complete(session.UserId, uploadId, overwrite);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "完成上传失败");
                    return;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, path = relativePath });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"完成上传失败: {ex.Message}");
            }
        });

        // API: chunked cancel (用户主动停止上传时清理会话)
        app.MapDelete("/api/chunked/cancel", async (HttpContext ctx) =>
        {
            var session = authService.GetSession(ctx);
            if (session == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("请先登录"); return; }
            var uploadId = ctx.Request.Query["uploadId"].ToString();
            if (string.IsNullOrEmpty(uploadId))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("缺少 uploadId");
                return;
            }
            chunkedUploadService.CancelSession(session.UserId, uploadId);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new { success = true });
        });

        // API: delete
        app.MapDelete("/api/files", (HttpContext ctx, string path) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            // 检查写权限
            if (!HasWritePermission(path, session, userService))
                return Results.Json(new { success = false, error = path.StartsWith("__shared__/") ? "共享文件夹为只读，不可删除文件" : "只读账户不可删除文件" }, statusCode: 403);
            var (fullPath, _) = ResolveOwnFilePath(path, session, service, userService);
            if (fullPath == null) return Results.Json(new { success = false, error = "无效路径" }, statusCode: 400);
            if (File.Exists(fullPath)) { File.Delete(fullPath); service.AddLog("INFO", "客户端", $"删文件 {System.IO.Path.GetFileName(fullPath)}"); return Results.Json(new { success = true }); }
            else if (Directory.Exists(fullPath)) { Directory.Delete(fullPath, true); service.AddLog("INFO", "客户端", $"删目录 {System.IO.Path.GetFileName(fullPath)}"); return Results.Json(new { success = true }); }
            return Results.Json(new { success = false, error = "文件不存在" }, statusCode: 404);
        });

        // API: mkdir
        app.MapPost("/api/files/mkdir", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            var session = authService.GetSession(ctx);
            if (session == null) return Results.Json(new { success = false, error = "请先登录" }, statusCode: 401);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            var dirName = data?.GetValueOrDefault("name", "");
            if (string.IsNullOrWhiteSpace(dirName)) return Results.Json(new { success = false, error = "目录名不能为空" }, statusCode: 400);
            var parentPath = FileShareService.NormalizeSubPath(data?.GetValueOrDefault("path", "") ?? "");
            // 检查写权限
            if (!HasWritePermission(parentPath, session, userService))
                return Results.Json(new { success = false, error = parentPath.StartsWith("__shared__/") ? "共享文件夹为只读，不可创建目录" : "只读账户不可创建目录" }, statusCode: 403);
            string baseDir;
            var (resolvedBase, sharedBaseDir) = ResolveOwnDirBase(parentPath, session, service, userService);
            if (resolvedBase == null) return Results.Json(new { success = false, error = "无效路径" }, statusCode: 400);
            baseDir = resolvedBase;
            string physicalParent = parentPath;
            if (!string.IsNullOrEmpty(parentPath) && (parentPath.StartsWith("__shared__/") || parentPath.StartsWith("__folder__/")))
            {
                var pfx = parentPath.StartsWith("__shared__/") ? "__shared__/" : "__folder__/";
                var idx = parentPath.IndexOf('/', pfx.Length);
                physicalParent = idx >= 0 ? parentPath.Substring(idx + 1) : "";
            }
            var fullPath = Path.Combine(baseDir, physicalParent, dirName);
            if (!FileShareService.IsPathUnderDirectory(baseDir, fullPath)) return Results.Json(new { success = false, error = "无效路径" }, statusCode: 403);
            if (!Directory.Exists(fullPath)) { Directory.CreateDirectory(fullPath); service.AddLog("INFO", "客户端", $"建目录 {dirName}"); }
            return Results.Json(new { success = true });
        });

        // API: select directory dialog (WinForms)（仅管理端/管理员）
        app.MapPost("/api/select-directory", async (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "Forbidden" }); return; }
            var tcs = new TaskCompletionSource<string?>();
            
            if (_uiContext == null)
            {
                service.AddLog("ERROR", "服务端", "无法打开目录对话框");
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = "UI 上下文未初始化" });
                return;
            }

            _uiContext.Post(_ =>
            {
                try
                {
                    var path = LanFileShare.Services.ModernFolderDialog.Show("选择客户端文件上传保存目录", service.GetFullUploadPath());
                    tcs.SetResult(path);
                }
                catch (Exception ex)
                {
                    service.AddLog("ERROR", "服务端", $"目录对话框: {ex.Message}");
                    tcs.SetException(ex);
                }
            }, null);
            
            try
            {
                var path = await tcs.Task;
                if (string.IsNullOrEmpty(path))
                    await ctx.Response.WriteAsJsonAsync(new { success = false, error = "未选择目录" });
                else
                    await ctx.Response.WriteAsJsonAsync(new { success = true, path = path });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                var err = ex.InnerException ?? ex;
                service.AddLog("ERROR", "服务端", $"目录对话框异常: {err.Message}");
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = err.Message ?? "未知错误" });
            }
        });

        app.MapPost("/api/select-folders", async (HttpContext ctx, NativeFolderPickerRequest request) =>
        {
            if (!CanUseNativeFolderPicker(ctx) || ctx.Request.Headers["X-LanFileShare-Folder-Picker"] != "1")
                return Results.Json(new { success = false, error = "请在运行 EXE 的电脑上选择文件夹。" }, statusCode: 403);
            if (!_nativeFolderPicker.Wait(0))
                return Results.Json(new { success = false, error = "请先关闭已打开的选择窗口。" }, statusCode: 409);

            var selection = new TaskCompletionSource<List<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _uiContext!.Post(_ =>
                {
                    try
                    {
                        var owner = _mainForm is { Visible: true } form ? form.Handle : IntPtr.Zero;
                        var title = request.Multiple ? "选择共享文件夹" : "选择默认共享文件夹";
                        var initialPath = request.InitialPath is { } path && Path.IsPathFullyQualified(path) ? path : null;
                        var paths = Services.ModernFolderDialog.ShowFolders(title, initialPath, owner, request.Multiple);
                        selection.TrySetResult(paths);
                    }
                    catch (Exception ex) { selection.TrySetException(ex); }
                }, null);

                var paths = await selection.Task;
                return Results.Json(new { success = true, paths = paths ?? new List<string>() });
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "服务端", $"打开 Windows 文件夹选择窗口失败: {ex.Message}");
                return Results.Json(new { success = false, error = "无法打开 Windows 选择窗口。请重试。" }, statusCode: 500);
            }
            finally { _nativeFolderPicker.Release(); }
        });

        // API: open explorer
        app.MapPost("/api/explorer/open", () =>
        {
            try
            {
                var dir = service.GetFullUploadPath();
                if (Directory.Exists(dir))
                {
                    if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                    else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", dir);
                    else System.Diagnostics.Process.Start("xdg-open", dir);
                    return Results.Json(new { success = true });
                }
                return Results.Json(new { success = false, error = "目录不存在" });
            }
            catch (Exception ex) { return Results.Json(new { success = false, error = ex.Message }); }
        });

        // API: public share status (无需认证，用于前端判断只读状态)
        app.MapGet("/api/public/status", () =>
        {
            if (!_fileShareEnabled) return Results.Json(new { enabled = false, readOnly = false }, statusCode: 503);
            if (!service.Settings.PublicShareEnabled) return Results.Json(new { enabled = false, readOnly = false }, statusCode: 403);
            return Results.Json(new
            {
                enabled = true,
                readOnly = service.Settings.PublicShareReadOnly,
                directories = service.GetPublicShareRoots().Select(folder => new { path = folder.Key, name = folder.Name }).ToList()
            });
        });

        // API: public share (无需认证)
        app.MapGet("/api/public/files", (string? path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            if (!service.Settings.PublicShareEnabled) return Results.Json(new { error = "公共共享已关闭" }, statusCode: 403);
            ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                service.UpdateUserOnline($"pub:{clientIp}", "公共用户", clientIp);

                var subPath = FileShareService.NormalizeSubPath(path ?? "");

                if (string.IsNullOrEmpty(subPath))
                {
                    // 根目录：列出所有共享文件夹
                    var items = new List<FileItem>();
                    foreach (var folder in service.GetPublicShareRoots())
                    {
                        if (!Directory.Exists(folder.Directory)) continue;
                        items.Add(new FileItem
                        {
                            Name = folder.Name,
                            Path = folder.Key,
                            IsDirectory = true,
                            Size = -1,
                            LastModified = Directory.GetLastWriteTime(folder.Directory)
                        });
                    }
                    return Results.Json(items);
                }
                var target = service.ResolvePublicShare(subPath);
                if (target == null || !Directory.Exists(target.Value.FullPath))
                    return Results.Json(new { error = "目录不存在" }, statusCode: 404);
                var (root, relativePath, fullPath) = target.Value;
                var files = service.ListFiles("", fullPath);
                var prefix = root.Key;
                if (!string.IsNullOrEmpty(relativePath))
                    prefix += "/" + relativePath.Replace('\\', '/');
                foreach (var file in files)
                {
                    file.Path = prefix + "/" + file.Path;
                    file.ParentPath = string.IsNullOrEmpty(file.ParentPath) ? prefix : prefix + "/" + file.ParentPath;
                }
                return Results.Json(files);
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "API", $"公共共享列出文件失败: {ex.Message}");
                return Results.Json(new { error = $"加载文件失败: {ex.Message}" }, statusCode: 500);
            }
        });

        // API: public file search (无需认证)
        app.MapGet("/api/public/files/search", (string q, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) return Results.Json(new { error = "文件共享服务已暂停" }, statusCode: 503);
            if (!service.Settings.PublicShareEnabled) return Results.Json(new { error = "公共共享已关闭" }, statusCode: 403);
            ctx.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(q))
                return Results.Json(new List<FileItem>());
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            service.UpdateUserOnline($"pub:{clientIp}", "公共用户", clientIp);
            var allResults = new List<FileItem>();
            foreach (var folder in service.GetPublicShareRoots())
            {
                var results = service.SearchFiles(q, folder.Directory);
                foreach (var r in results)
                {
                    r.Path = folder.Key + "/" + r.Path;
                    if (!string.IsNullOrEmpty(r.ParentPath))
                        r.ParentPath = folder.Key + "/" + r.ParentPath;
                    else
                        r.ParentPath = folder.Key;
                }
                allResults.AddRange(results);
            }
            return Results.Json(allResults);
        });

        app.MapGet("/api/public/files/download", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            var fullPath = ResolvePublicSharePath(path, service);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            service.AddLog("DOWN", "公共共享", $"下载 {System.IO.Path.GetFileName(fullPath)}");
            await SendFileWithRangeAsync(ctx, fullPath, service, $"pub:{clientIp}", "公共用户", clientIp);
        });

        app.MapMethods("/api/public/files/download", new[] { "HEAD" }, (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; return Task.CompletedTask; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            var fullPath = ResolvePublicSharePath(path, service);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
            var fileInfo = new FileInfo(fullPath);
            var fileName = System.IO.Path.GetFileName(fullPath);
            ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"");
            ctx.Response.Headers.Append("Accept-Ranges", "bytes");
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = fileInfo.Length;
            return Task.CompletedTask;
        });

        // API: public file preview (图片/视频内联播放，支持 Range)
        app.MapGet("/api/public/files/preview", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            var fullPath = ResolvePublicSharePath(path, service);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            service.AddLog("VIEW", "公共共享", $"预览 {Path.GetFileName(fullPath)}");
            await SendFilePreviewAsync(ctx, fullPath, service, $"pub:{clientIp}", "公共用户", clientIp);
        });
        // API: public file preview raw (返回文件原始内容，供前端 JS 库解析)
        app.MapGet("/api/public/files/preview/raw", async (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            var fullPath = ResolvePublicSharePath(path, service);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("文件不存在"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > 50 * 1024 * 1024) { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync("文件过大，不支持预览"); return; }
            try
            {
                SetPreviewHeaders(ctx.Response, fullPath);
                ctx.Response.ContentLength = fileInfo.Length;
                // 使用 FileShare.Read 允许其他程序同时打开文件（如 Excel 正在编辑时仍可预览）
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(ctx.Response.Body);
                }
            }
            catch (IOException ex) when (IsFileLockedException(ex))
            {
                service.AddLog("WARN", "公共共享", $"raw预览文件被占用 {Path.GetFileName(fullPath)}: {ex.Message}");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength = null;
                await ctx.Response.WriteAsync("文件被其他程序占用，请关闭文件后重试");
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", "公共共享", $"raw预览失败 {Path.GetFileName(fullPath)}: {ex.Message}");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength = null;
                await ctx.Response.WriteAsync("文件读取失败");
            }
        });
        app.MapMethods("/api/public/files/preview", new[] { "HEAD" }, (string path, HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; return Task.CompletedTask; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            var fullPath = ResolvePublicSharePath(path, service);
            if (fullPath == null || !File.Exists(fullPath)) { ctx.Response.StatusCode = 404; return Task.CompletedTask; }
            var fileInfo = new FileInfo(fullPath);
            var fileName = Path.GetFileName(fullPath);
            ctx.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{Uri.EscapeDataString(fileName)}\"");
            ctx.Response.Headers.Append("Accept-Ranges", "bytes");
            SetPreviewHeaders(ctx.Response, fullPath);
            ctx.Response.ContentLength = fileInfo.Length;
            return Task.CompletedTask;
        });

        // API: public share upload (无需认证)
        app.MapPost("/api/public/files/upload", async (HttpContext ctx) =>
        {
            try
            {
                if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
                if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
                if (service.Settings.PublicShareReadOnly) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已设置为只读，禁止上传"); return; }
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
                if (!ctx.Request.HasFormContentType) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("请使用 multipart/form-data"); return; }
                var form = await ctx.Request.ReadFormAsync();
                var subPath = FileShareService.NormalizeSubPath(form["path"].FirstOrDefault() ?? "");
                var files = form.Files;
                if (files.Count == 0) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("未选择文件"); return; }
                // 禁止在根目录上传
                if (string.IsNullOrEmpty(subPath)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("根目录不允许上传，请进入具体文件夹后再上传"); return; }
                var target = service.ResolvePublicShare(subPath);
                if (target == null || !Directory.Exists(target.Value.FullPath)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("目标目录不存在"); return; }
                var (shareRoot, _, targetDir) = target.Value;
                var baseDir = shareRoot.Directory;

                service.AddLog("INFO", "公共共享", $"上传 {files.Count} 个文件");

                const long publicShareMaxFileSize = 500L * 1024 * 1024; // 500MB 单文件上限
                var uploaded = new List<string>();
                var skipped = new List<string>();
                var pubUserId = $"pub:{clientIp}";
                service.TrackUserUploaded(pubUserId, "公共用户", clientIp, 0);
                foreach (var file in files)
                {
                    try
                    {
                        if (file.Length > publicShareMaxFileSize)
                        {
                            skipped.Add($"{file.FileName} ({FormatSize(file.Length)})");
                            service.AddLog("WARN", $"公共共享跳过超大文件: {file.FileName} ({FormatSize(file.Length)})");
                            continue;
                        }
                        var relativePath = file.FileName.Replace('/', Path.DirectorySeparatorChar);
                        var safeRelativePath = SanitizePath(relativePath);
                        if (string.IsNullOrWhiteSpace(safeRelativePath))
                        {
                            service.AddLog("WARN", $"跳过无效文件名: {file.FileName}");
                            continue;
                        }
                        var destPath = Path.Combine(targetDir, safeRelativePath);
                        if (!FileShareService.IsPathUnderDirectory(baseDir, destPath))
                        {
                            service.AddLog("WARN", $"跳过越界路径: {safeRelativePath}");
                            continue;
                        }
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);

                        // 获取上传限速 (MB/s)
                        var (upLimitMBps, _) = service.GetBandwidthLimit(pubUserId);

                        using var stream = new FileStream(destPath, FileMode.Create);
                        if (upLimitMBps > 0)
                        {
                            // 带限速的流拷贝
                            var buffer = new byte[65536];
                            long totalCopied = 0;
                            long batchBytes = 0;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int read;
                            while ((read = await file.OpenReadStream().ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await stream.WriteAsync(buffer, 0, read);
                                totalCopied += read;
                                batchBytes += read;

                                // 每约 256KB 更新一次速度追踪
                                if (batchBytes >= 262144)
                                {
                                    service.TrackUserUploadBytes(pubUserId, batchBytes);
                                    batchBytes = 0;
                                }

                                var targetMs = totalCopied / (upLimitMBps * 1048576.0) * 1000.0;
                                var delay = targetMs - sw.ElapsedMilliseconds;
                                if (delay > 0)
                                    await Task.Delay((int)Math.Min(delay, 1000));
                            }
                            // 补上尾部不足 256KB 的字节
                            if (batchBytes > 0)
                                service.TrackUserUploadBytes(pubUserId, batchBytes);
                        }
                        else
                        {
                            await file.CopyToAsync(stream);
                            service.TrackUserUploadBytes(pubUserId, file.Length);
                        }
                        service.AddLog("UPLD", "公共共享", $"{safeRelativePath} ({FormatSize(file.Length)})");
                        uploaded.Add(safeRelativePath);
                    }
                    catch (Exception ex)
                    {
                        service.AddLog("ERROR", $"上传文件失败 [{file.FileName}]: {ex.Message}");
                    }
                }

                if (uploaded.Count == 0)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync("所有文件上传失败，请检查保存目录权限或文件名是否合法");
                    return;
                }

                service.AddLog("INFO", "公共共享", $"上传完成 {uploaded.Count}/{files.Count}" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个超大文件" : ""));
                await ctx.Response.WriteAsJsonAsync(new { success = true, files = uploaded, skipped = skipped });
            }
            catch (InvalidDataException ex)
            {
                service.AddLog("ERROR", $"上传请求解析失败: {ex.Message}");
                ctx.Response.StatusCode = 413;
                await ctx.Response.WriteAsync("上传数据过大，单个请求请不超过 200GB");
            }
            catch (Exception ex)
            {
                service.AddLog("ERROR", $"上传处理失败: {ex.Message}");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"上传失败: {ex.Message}");
            }
        });

        // API: public chunked upload (公共共享分块上传，无需认证)
        app.MapPost("/api/public/chunked/init", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            if (service.Settings.PublicShareReadOnly) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已设置为只读，禁止上传"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var json = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var fileName = root.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
                var fileSize = root.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out var sz) ? sz : 0L;
                var subPath = root.TryGetProperty("subPath", out var sp) ? sp.GetString() ?? "" : "";
                var accessPath = subPath;
                var contentHash = root.TryGetProperty("contentHash", out var ch) ? ch.GetString() : null;

                var pubUserId = $"pub:{clientIp}";
                // 初始化速度追踪器（显示为"公共用户"）
                service.TrackUserUploaded(pubUserId, "公共用户", clientIp, 0);

                var target = service.ResolvePublicShare(subPath);
                if (target == null || !Directory.Exists(target.Value.FullPath))
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("目标目录不存在");
                    return;
                }
                var (shareRoot, relativePath, _) = target.Value;

                var (ok, error, uploadId, uploadedBytes, chunkSize, fileExists) = chunkedUploadService.Init(
                    pubUserId, "__public__", relativePath, fileName, fileSize, shareRoot.Directory, contentHash, accessPath);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "初始化失败");
                    return;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, uploadId, uploadedBytes, chunkSize, fileExists });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"初始化失败: {ex.Message}");
            }
        });

        app.MapPost("/api/public/chunked/write", async (HttpContext ctx) =>
        {
            var bodyLimit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = UploadLimits.MaxChunkRequestSize;
            if (ctx.Request.ContentLength > UploadLimits.MaxChunkRequestSize)
            { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync("分块过大。请重新选择文件上传。"); return; }

            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            if (service.Settings.PublicShareReadOnly) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已设置为只读，禁止上传"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (service.IsIPBlacklisted(clientIp)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("您已被封禁"); return; }
            try
            {
                var uploadId = ctx.Request.Query["uploadId"].ToString();
                if (!long.TryParse(ctx.Request.Query["offset"], out var offset)) offset = 0;
                if (string.IsNullOrEmpty(uploadId))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("缺少 uploadId");
                    return;
                }

                var pubUserId = $"pub:{clientIp}";
                var (ok, error, uploadedBytes) = await chunkedUploadService.WriteChunkAsync(
                    pubUserId, uploadId, offset, ctx.Request.Body, ctx.RequestAborted);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "写入分块失败");
                    return;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, uploadedBytes });
            }
            catch (InvalidDataException ex)
            { ctx.Response.StatusCode = 413; await ctx.Response.WriteAsync(ex.Message); }
            catch (BadHttpRequestException ex)
            { ctx.Response.StatusCode = ex.StatusCode; }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"写入分块失败: {ex.Message}");
            }
        });

        app.MapPost("/api/public/chunked/complete", async (HttpContext ctx) =>
        {
            if (!_fileShareEnabled) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("文件共享服务已暂停"); return; }
            if (!service.Settings.PublicShareEnabled) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已关闭"); return; }
            if (service.Settings.PublicShareReadOnly) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("公共共享已设置为只读，禁止上传"); return; }
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var json = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var uploadId = root.TryGetProperty("uploadId", out var uid) ? uid.GetString() ?? "" : "";
                var overwrite = root.TryGetProperty("overwrite", out var ow) && ow.ValueKind == JsonValueKind.True;

                var pubUserId = $"pub:{clientIp}";
                var (ok, error, relativePath) = chunkedUploadService.Complete(pubUserId, uploadId, overwrite);
                if (!ok)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(error ?? "完成上传失败");
                    return;
                }
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new { success = true, path = relativePath });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync($"完成上传失败: {ex.Message}");
            }
        });

        // API: public chunked cancel (公共共享取消上传)
        app.MapDelete("/api/public/chunked/cancel", async (HttpContext ctx) =>
        {
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var uploadId = ctx.Request.Query["uploadId"].ToString();
            if (!string.IsNullOrEmpty(uploadId))
            {
                var pubUserId = $"pub:{clientIp}";
                chunkedUploadService.CancelSession(pubUserId, uploadId);
            }
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new { success = true });
        });

        // API: update check / apply / restart（仅管理端/管理员）
        app.MapGet("/api/update/check", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var result = await updateService.CheckForUpdatesAsync(ct);
            return Results.Json(result);
        });

        app.MapPost("/api/update/apply", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            var result = await updateService.ApplyUpdatesAsync(ct);
            return Results.Json(result);
        });

        app.MapPost("/api/update/restart", (HttpContext ctx) =>
        {
            if (!IsAdminRequest(ctx, authService, userService)) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            if (!updateService.LaunchRestartUpdate())
                return Results.Json(new { success = false, error = "没有待重启的更新" });
            UpdateService.ScheduleProcessExit(_cts, _mainForm);
            return Results.Json(new { success = true });
        });
    }

    private static Icon LoadIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppPaths.ResourceDirectory, "icon.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath);
        }
        catch { }
        return SystemIcons.Application;
    }

    private static string FormatSpeed(double bps)
    {
        if (bps < 1024) return $"{bps:0.#} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0.#} KB/s";
        if (bps < 1024 * 1024 * 1024) return $"{bps / (1024 * 1024):0.#} MB/s";
        return $"{bps / (1024 * 1024 * 1024):0.#} GB/s";
    }

    // 解析 __shared__/ 虚拟路径
    private static (string? fullPath, string? baseDir, SharedFolderEntry? entry) ResolveSharedPath(string path, string userId, UserService userService)
    {
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__shared__/"))
        {
            var parts = path.Substring("__shared__/".Length).Split('/', 2);
            var shareId = parts[0];
            var subPath = parts.Length > 1 ? parts[1] : "";
            var sharedEntry = userService.GetSharedFolders(userId)
                .FirstOrDefault(sf => sf.Id == shareId);
            if (sharedEntry == null)
                return (null, null, null);

            // 如果是文件共享，只允许直接访问文件本身（没有子路径）
            if (sharedEntry.IsFile)
            {
                if (string.IsNullOrEmpty(subPath) && File.Exists(sharedEntry.SourcePath))
                    return (sharedEntry.SourcePath, Path.GetDirectoryName(sharedEntry.SourcePath) ?? "", sharedEntry);
                return (null, null, null);
            }

            // 文件夹共享
            if (!Directory.Exists(sharedEntry.SourcePath))
                return (null, null, null);
            var fullPath = string.IsNullOrEmpty(subPath)
                ? sharedEntry.SourcePath
                : Path.GetFullPath(Path.Combine(sharedEntry.SourcePath, SanitizePath(subPath)));
            var normalizedSharedRoot = Path.GetFullPath(sharedEntry.SourcePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(normalizedSharedRoot) &&
                fullPath != Path.GetFullPath(sharedEntry.SourcePath))
                return (null, null, null);
            return (fullPath, sharedEntry.SourcePath, sharedEntry);
        }
        return (null, null, null);
    }

    // 解析 __folder__/N 虚拟路径（多目录共享根切换）
    private static bool TryResolveOwnFolderPath(string path, ClientSession session, out string baseDir, out string subPath, out int folderIndex)
    {
        baseDir = "";
        subPath = "";
        folderIndex = -1;
        if (string.IsNullOrEmpty(path) || !path.StartsWith("__folder__/"))
            return false;

        var parts = path.Substring("__folder__/".Length).Split('/', 2);
        if (!int.TryParse(parts[0], out folderIndex) || folderIndex < 0 || folderIndex >= session.Folders.Count)
            return false;

        baseDir = session.Folders[folderIndex];
        subPath = parts.Length > 1 ? parts[1] : "";
        return true;
    }

    // 不需要 folderIndex 的便捷重载
    private static bool TryResolveOwnFolderPath(string path, ClientSession session, out string baseDir, out string subPath)
        => TryResolveOwnFolderPath(path, session, out baseDir, out subPath, out _);

    // path -> 物理完整路径，支持 __shared__/、__folder__/ 虚拟前缀
    private static (string? fullPath, string? baseDir) ResolveOwnFilePath(string path, ClientSession session, FileShareService service, UserService userService)
    {
        // 共享路径优先
        var (sharedFull, sharedBase, _) = ResolveSharedPath(path, session.UserId, userService);
        if (sharedFull != null)
            return (sharedFull, sharedBase);

        // 虚拟共享根（多目录共享中非首个根）
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__folder__/"))
        {
            if (!TryResolveOwnFolderPath(path, session, out var rootBase, out var rootSub))
                return (null, null);
            var full = string.IsNullOrEmpty(rootSub) ? rootBase : Path.Combine(rootBase, rootSub);
            return (full, rootBase);
        }

        // 普通路径：使用第一个共享根
        if (session.Folders == null || session.Folders.Count == 0)
            return (null, null);
        var fp = service.GetFullPath(path, session.Folders[0]);
        return (fp, session.Folders[0]);
    }

    // path -> 物理基础目录，用于上传/mkdir 等场景
    private static (string? baseDir, string? sharedEntryBase) ResolveOwnDirBase(string? path, ClientSession session, FileShareService service, UserService userService)
    {
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__shared__/"))
        {
            var (_, baseDir, _) = ResolveSharedPath(path, session.UserId, userService);
            return (baseDir, baseDir);
        }
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__folder__/"))
        {
            if (!TryResolveOwnFolderPath(path, session, out var rootBase, out _))
                return (null, null);
            return (rootBase, null);
        }
        if (session.Folders == null || session.Folders.Count == 0)
            return (null, null);
        var b = service.ResolveBaseDirectory(session.Folders[0]);
        return (b, null);
    }

    // 路径写权限检查：虚拟根/共享路径/普通路径各自判断
    private static bool HasWritePermission(string path, ClientSession session, UserService userService)
    {
        // 虚拟共享根路径：普通共享根与原始逻辑一致（受用户自身 ReadOnly 限制）
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__folder__/"))
            return !session.ReadOnly;
        // 如果是共享文件夹路径，检查共享条目的 WritePermission
        if (!string.IsNullOrEmpty(path) && path.StartsWith("__shared__/"))
        {
            var parts = path.Substring("__shared__/".Length).Split('/', 2);
            var shareId = parts[0];
            var sharedEntry = userService.GetSharedFolders(session.UserId)
                .FirstOrDefault(sf => sf.Id == shareId);
            if (sharedEntry == null)
                return false;
            return sharedEntry.WritePermission;
        }
        // 普通路径：检查用户自身的 ReadOnly 属性
        return !session.ReadOnly;
    }

    private static string SanitizePath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safe = parts
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "..")
            .Select(p => string.Join("_", p.Split(Path.GetInvalidFileNameChars())));
        return string.Join(Path.DirectorySeparatorChar.ToString(), safe);
    }

    // Windows 盘符 "D:" 会被 GetFullPath 解析成工作目录而不是根目录，补个反斜杠
    private static string NormalizeBrowseFolderPath(string path)
    {
        if (path.Length == 2 && path[1] == ':')
            path += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path);
    }

    // 公共共享路径解析，支持多目录
    private static string? ResolvePublicSharePath(string path, FileShareService service) =>
        service.ResolvePublicShare(path)?.FullPath;

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        // 复制文件
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        // 递归复制子目录
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSubDir);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.#} {sizes[order]}";
    }

    // 启动时预热文件索引，免得首次搜索太慢
    private static void BuildAllStartupIndices()
    {
        try
        {
            var us = _userService;
            var svc = _service;
            if (us == null || svc == null) return;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 所有用户主目录
            foreach (var user in us.ListUsers())
            {
                if (!string.IsNullOrEmpty(user.Folder))
                    paths.Add(Path.GetFullPath(user.Folder));
            }

            // 2. 所有已存在的共享目录
            foreach (var user in us.ListUsers())
            {
                var shared = us.GetSharedFolders(user.Id);
                foreach (var sf in shared)
                {
                    if (Directory.Exists(sf.SourcePath))
                        paths.Add(Path.GetFullPath(sf.SourcePath));
                }
            }

            // 3. 公共共享目录（如果启用）
            if (svc.Settings.PublicShareEnabled)
            {
                foreach (var pubPath in svc.GetPublicSharePaths())
                    if (Directory.Exists(pubPath))
                        paths.Add(pubPath);
            }

            svc.AddLog("INFO", "INDEX", $"启动预构建 {paths.Count} 个目录的文件索引...");

            // 并发构建，不阻塞启动流程
            foreach (var path in paths)
            {
                _ = svc.BuildIndexAsync(path);
            }
        }
        catch (Exception ex)
        {
            _service?.AddLog("WARN", "INDEX", $"启动预构建索引异常: {ex.Message}");
        }
    }

    // 带带宽限制的流拷贝
    private static async Task CopyStreamWithThrottleAsync(Stream source, Stream destination, double limitMBps, CancellationToken ct, Action<long>? onProgress = null)
    {
        // 无限速且无进度回调时直接 CopyToAsync
        if (limitMBps <= 0 && onProgress == null)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }
        var buffer = new byte[65536];
        long totalCopied = 0;
        long batchBytes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destination.WriteAsync(buffer, 0, read, ct);
            totalCopied += read;
            batchBytes += read;

            // 每约 256KB 触发一次进度回调
            if (onProgress != null && batchBytes >= 262144)
            {
                onProgress(batchBytes);
                batchBytes = 0;
            }

            if (limitMBps > 0)
            {
                var targetMs = totalCopied / (limitMBps * 1048576.0) * 1000.0;
                var delay = targetMs - sw.ElapsedMilliseconds;
                if (delay > 0)
                    await Task.Delay((int)Math.Min(delay, 1000), ct);
            }
        }
        // 补上尾部不足 256KB 的字节
        if (onProgress != null && batchBytes > 0)
            onProgress(batchBytes);
    }

    // MIME 类型映射
    private static readonly Dictionary<string, string> _mimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图片
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".tiff"] = "image/tiff", [".tif"] = "image/tiff",
        // 视频
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg",
        [".ogg"] = "video/ogg", [".mov"] = "video/quicktime", [".avi"] = "video/x-msvideo",
        [".mkv"] = "video/x-matroska", [".flv"] = "video/x-flv", [".wmv"] = "video/x-ms-wmv",
        [".m4v"] = "video/x-m4v", [".3gp"] = "video/3gpp", [".ts"] = "video/mp2t",
        // 音频
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".flac"] = "audio/flac",
        [".aac"] = "audio/aac", [".m4a"] = "audio/mp4", [".opus"] = "audio/opus",
        // 文档
        [".pdf"] = "application/pdf", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        // 文本/代码
        [".txt"] = "text/plain", [".csv"] = "text/csv", [".md"] = "text/markdown",
        [".json"] = "application/json", [".xml"] = "application/xml",
        [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css",
        [".js"] = "text/javascript", [".ts"] = "text/typescript",
        [".py"] = "text/x-python", [".java"] = "text/x-java",
        [".c"] = "text/x-c", [".cpp"] = "text/x-c++", [".cs"] = "text/x-csharp",
        [".go"] = "text/x-go", [".rs"] = "text/x-rust", [".php"] = "text/x-php",
        [".rb"] = "text/x-ruby", [".sql"] = "text/x-sql",
        [".yaml"] = "text/yaml", [".yml"] = "text/yaml",
        [".ini"] = "text/plain", [".cfg"] = "text/plain", [".log"] = "text/plain",
        [".sh"] = "text/x-sh", [".bat"] = "text/plain", [".ps1"] = "text/plain",
        [".rtf"] = "application/rtf", [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
    };

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path);
        return _mimeMap.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }

    private static void SetPreviewHeaders(HttpResponse response, string fullPath)
    {
        var mimeType = GetMimeType(fullPath);
        response.ContentType = mimeType;
        if (mimeType != "text/html") return;

        // Apply the same isolation when someone opens a preview URL directly.
        response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'unsafe-inline'; " +
            "img-src data:; font-src data:; media-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cache-Control"] = "no-store";
    }

    // 检测是否是文件被其他进程锁定的异常
    private static bool IsFileLockedException(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        // 0x20 = ERROR_SHARING_VIOLATION, 0x21 = ERROR_LOCK_VIOLATION
        if (code == 0x0020 || code == 0x0021) return true;
        var msg = ex.Message;
        if (msg.Contains("another process") || msg.Contains("being used by another"))
            return true;
        // 中文 Windows 异常消息
        if (msg.Contains("另一个进程") || msg.Contains("正由另一进程"))
            return true;
        return false;
    }

    // 验证是否是管理端请求（本地回环或管理员 session）
    private static bool CanUseNativeFolderPicker(HttpContext ctx)
    {
        if (_uiContext == null || _mainForm == null
            || ctx.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote))
            return false;

        var host = ctx.Request.Host.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    private static bool IsAdminRequest(HttpContext ctx, AuthService authService, UserService userService)
    {
        // 方式1: 本地回环请求 → 直接放行（只有本机 WebView2 管理页面能发本地回环请求）
        if (ctx.Connection.RemoteIpAddress != null &&
            System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress))
            return true;

        // 方式2: 远程管理员用户通过 session 登录
        var session = authService.GetSession(ctx);
        if (session != null)
        {
            var user = userService.FindById(session.UserId);
            if (user != null && user.IsAdmin)
                return true;
        }

        return false;
    }

    // 支持 HTTP Range 的断点续传下载
    private static async Task SendFileWithRangeAsync(HttpContext ctx, string fullPath, FileShareService service, string? userId = null, string? username = null, string? ip = null)
    {
        var fileInfo = new FileInfo(fullPath);
        var fileName = System.IO.Path.GetFileName(fullPath);
        var fileLength = fileInfo.Length;
        var rangeHeader = ctx.Request.Headers.Range.ToString();

        ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fileName)}\"");
        ctx.Response.Headers.Append("Accept-Ranges", "bytes");
        ctx.Response.ContentType = "application/octet-stream";

        // 获取下载限速 (MB/s)
        var (_, downLimitMBps) = !string.IsNullOrEmpty(userId)
            ? service.GetBandwidthLimit(userId)
            : (0.0, 0.0);

        // 解析 Range
        long start = 0, end = fileLength - 1;
        int statusCode = 200;
        bool useRange = false;

        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeValue = rangeHeader["bytes=".Length..];
            var parts = rangeValue.Split('-');
            if (parts.Length == 2)
            {
                if (!long.TryParse(parts[0], out start)) start = 0;
                if (string.IsNullOrEmpty(parts[1]))
                    end = fileLength - 1;
                else if (!long.TryParse(parts[1], out end))
                    end = fileLength - 1;

                if (start >= fileLength)
                {
                    ctx.Response.StatusCode = 416; // Range Not Satisfiable
                    ctx.Response.Headers.Append("Content-Range", $"bytes */{fileLength}");
                    return;
                }
                if (end >= fileLength) end = fileLength - 1;
                useRange = true;
                statusCode = 206;
            }
        }

        var contentLength = end - start + 1;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentLength = contentLength;

        if (useRange)
            ctx.Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileLength}");

        service.TrackDownloaded(contentLength);

        // 无限速且无Range时使用高效 SendFileAsync（零拷贝）
        if (downLimitMBps <= 0 && !useRange)
        {
            if (!string.IsNullOrEmpty(userId))
                service.TrackUserDownloaded(userId, username ?? "", ip ?? "", contentLength);
            await ctx.Response.SendFileAsync(fullPath);
            return;
        }

        // 限速或 Range → 手动分段发送（含实时速度追踪）
        // 先用 0 字节初始化 tracker（记录 username/ip），后续通过 TrackUserDownloadBytes 渐进累加
        if (!string.IsNullOrEmpty(userId))
            service.TrackUserDownloaded(userId, username ?? "", ip ?? "", 0);

        await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[65536]; // 64KB buffer
        long remaining = contentLength;
        long totalSent = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long batchBytes = 0;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var read = await fs.ReadAsync(buffer, 0, toRead, ctx.RequestAborted);
            if (read == 0) break;
            await ctx.Response.Body.WriteAsync(buffer, 0, read, ctx.RequestAborted);
            remaining -= read;
            totalSent += read;
            batchBytes += read;
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // 每约 256KB 更新一次速度追踪（避免每个 64KB chunk 都原子操作）
            if (!string.IsNullOrEmpty(userId) && batchBytes >= 262144)
            {
                service.TrackUserDownloadBytes(userId, batchBytes);
                batchBytes = 0;
            }

            // 下载限速节流
            if (downLimitMBps > 0)
            {
                var targetMs = totalSent / (downLimitMBps * 1048576.0) * 1000.0;
                var delay = targetMs - sw.ElapsedMilliseconds;
                if (delay > 0)
                    await Task.Delay((int)Math.Min(delay, 1000), ctx.RequestAborted);
            }
        }

        // 补上尾部不足 256KB 的字节
        if (!string.IsNullOrEmpty(userId) && batchBytes > 0)
            service.TrackUserDownloadBytes(userId, batchBytes);
    }

    // 浏览器内预览（inline + 正确的 MIME，支持 Range）
    private static async Task SendFilePreviewAsync(HttpContext ctx, string fullPath, FileShareService service, string? userId = null, string? username = null, string? ip = null)
    {
        var fileInfo = new FileInfo(fullPath);
        var fileName = Path.GetFileName(fullPath);
        var fileLength = fileInfo.Length;
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        ctx.Response.Headers.Append("Content-Disposition", $"inline; filename=\"{Uri.EscapeDataString(fileName)}\"");
        ctx.Response.Headers.Append("Accept-Ranges", "bytes");
        SetPreviewHeaders(ctx.Response, fullPath);

        var (_, downLimitMBps) = !string.IsNullOrEmpty(userId)
            ? service.GetBandwidthLimit(userId)
            : (0.0, 0.0);

        long start = 0, end = fileLength - 1;
        int statusCode = 200;
        bool useRange = false;

        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
        {
            var rangeValue = rangeHeader["bytes=".Length..];
            var parts = rangeValue.Split('-');
            if (parts.Length == 2)
            {
                if (!long.TryParse(parts[0], out start)) start = 0;
                if (string.IsNullOrEmpty(parts[1]))
                    end = fileLength - 1;
                else if (!long.TryParse(parts[1], out end))
                    end = fileLength - 1;

                if (start >= fileLength)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers.Append("Content-Range", $"bytes */{fileLength}");
                    return;
                }
                if (end >= fileLength) end = fileLength - 1;
                useRange = true;
                statusCode = 206;
            }
        }

        var contentLength = end - start + 1;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentLength = contentLength;

        if (useRange)
            ctx.Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileLength}");

        service.TrackDownloaded(contentLength);

        if (downLimitMBps <= 0 && !useRange)
        {
            if (!string.IsNullOrEmpty(userId))
                service.TrackUserDownloaded(userId, username ?? "", ip ?? "", contentLength);
            await ctx.Response.SendFileAsync(fullPath);
            return;
        }

        if (!string.IsNullOrEmpty(userId))
            service.TrackUserDownloaded(userId, username ?? "", ip ?? "", 0);

        await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[65536];
        long remaining = contentLength;
        long totalSent = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long batchBytes = 0;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var read = await fs.ReadAsync(buffer, 0, toRead, ctx.RequestAborted);
            if (read == 0) break;
            await ctx.Response.Body.WriteAsync(buffer, 0, read, ctx.RequestAborted);
            remaining -= read;
            totalSent += read;
            batchBytes += read;
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            if (!string.IsNullOrEmpty(userId) && batchBytes >= 262144)
            {
                service.TrackUserDownloadBytes(userId, batchBytes);
                batchBytes = 0;
            }

            if (downLimitMBps > 0)
            {
                var targetMs = totalSent / (downLimitMBps * 1048576.0) * 1000.0;
                var delay = targetMs - sw.ElapsedMilliseconds;
                if (delay > 0)
                    await Task.Delay((int)Math.Min(delay, 1000), ctx.RequestAborted);
            }
        }

        if (!string.IsNullOrEmpty(userId) && batchBytes > 0)
            service.TrackUserDownloadBytes(userId, batchBytes);
    }
}

internal sealed record NativeFolderPickerRequest(string? InitialPath, bool Multiple = true);

public class ChatFileMeta
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FilePath { get; set; } = "";
    public string Uploader { get; set; } = "";
    public long UploadTime { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
}
