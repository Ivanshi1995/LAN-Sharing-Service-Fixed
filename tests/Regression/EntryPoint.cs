using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanFileShare;

namespace LanFileShare.Regression;

internal static class EntryPoint
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "regression-" + Guid.NewGuid().ToString("N"));
    private static readonly List<object> Results = new();
    private static int _failed;
    private static FileShareService Files = null!;
    private static UserService Users = null!;
    private static ChunkedUploadService Uploads = null!;
    private static int _userNumber;
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static void Check(bool condition, string message = "断言失败") { if (!condition) throw new Exception(message); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { name, passed = true }); Console.WriteLine("通过：" + name); }
        catch (Exception ex) { _failed++; Results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("失败：" + name + " " + ex.Message); }
    }
    private static UserDto NewUser()
    {
        var id = Interlocked.Increment(ref _userNumber);
        var folders = new[] { Path.Combine(Root, "u" + id, "a"), Path.Combine(Root, "u" + id, "b") };
        foreach (var path in folders) Directory.CreateDirectory(path);
        var result = Users.CreateUser("test" + Guid.NewGuid().ToString("N")[..12], "test-password", folders.ToList(), true);
        Check(result.success, result.error ?? "创建测试账户失败");
        return result.user!;
    }
    private static (string id, byte[] data) Init(UserDto user, string name = "file.txt", int root = 0, byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes("测试文件内容");
        var result = Uploads.Init(user.Id, user.Folders[0], "", name, bytes.Length,
            user.Folders[root], Hash(bytes), "__folder__/" + root);
        Check(result.ok, result.error ?? "初始化失败");
        return (result.uploadId!, bytes);
    }
    private static async Task Fill(UserDto user, string id, byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += UploadLimits.ChunkSize)
        {
            using var stream = new MemoryStream(data, offset, Math.Min(UploadLimits.ChunkSize, data.Length - offset));
            var result = await Uploads.WriteChunkAsync(user.Id, id, offset, stream, CancellationToken.None);
            Check(result.ok, result.error ?? "写入失败");
        }
    }
    private static string MetaPath(string id) => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, ".upload-sessions"), id + ".json", SearchOption.AllDirectories).Single();
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(40); }
        Check(false, "等待文件索引更新超时");
    }

    public static async Task<int> Main(string[] args)
    {
        var reportPath = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
        Directory.CreateDirectory(Root);
        Directory.SetCurrentDirectory(Root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<FileShareService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<UploadAccessPolicy>();
        builder.Services.AddSingleton<ChunkedUploadService>();
        builder.Services.AddSingleton<ConcurrentBag<StreamWriter>>();
        await using var app = builder.Build();
        Files = app.Services.GetRequiredService<FileShareService>();
        Users = app.Services.GetRequiredService<UserService>();
        Uploads = app.Services.GetRequiredService<ChunkedUploadService>();
        typeof(Program).GetField("_service", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, Files);
        typeof(Program).GetField("_sseClients", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, app.Services.GetRequiredService<ConcurrentBag<StreamWriter>>());
        typeof(Program).GetMethod("ConfigureApp", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { app });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        NewUser(); // Create an actual users.json containing a password hash.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "settings.json"), "{\"secret\":true}");
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "SharedFiles"));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "SharedFiles", "private.txt"), "secret");

        foreach (var path in new[] { "/users.json", "/settings.json", "/SharedFiles/private.txt", "/lan-file-share-server.html" })
            await Run("静态访问拒绝 " + path, async () => Check((await http.GetAsync(path)).StatusCode == HttpStatusCode.NotFound));
        await Run("离线资源仍可读取", async () => Check((await http.GetAsync("/CDN/upload-fingerprint.js")).IsSuccessStatusCode));
        await Run("登录页面仍可读取", async () => Check((await http.GetAsync("/")).IsSuccessStatusCode));
        await Run("私人文件仍需登录", async () => Check((await http.GetAsync("/api/files/download?path=file.txt")).StatusCode == HttpStatusCode.Unauthorized));

        await Run("不同目标目录隔离续传会话", async () =>
        {
            var user = NewUser(); var a = Init(user); var b = Init(user, root: 1);
            Check(a.id != b.id);
            await Fill(user, a.id, a.data); await Fill(user, b.id, b.data);
            Check(Uploads.Complete(user.Id, b.id).ok); Check(Uploads.Complete(user.Id, a.id).ok);
            Check(File.ReadAllBytes(Path.Combine(user.Folders[0], "file.txt")).SequenceEqual(a.data));
            Check(File.ReadAllBytes(Path.Combine(user.Folders[1], "file.txt")).SequenceEqual(b.data));
        });
        await Run("相同文件可以断点续传", async () =>
        {
            var user = NewUser(); var item = Init(user); using var prefix = new MemoryStream(item.data[..3]);
            Check((await Uploads.WriteChunkAsync(user.Id, item.id, 0, prefix, default)).ok);
            var resumed = Uploads.Init(user.Id, user.Folders[0], "", "file.txt", item.data.Length, user.Folders[0], Hash(item.data), "__folder__/0");
            Check(resumed.ok && resumed.uploadId == item.id && resumed.uploadedBytes == 3);
            using var suffix = new MemoryStream(item.data[3..]);
            Check((await Uploads.WriteChunkAsync(user.Id, item.id, 3, suffix, default)).ok);
            Check(Uploads.Complete(user.Id, item.id).ok);
        });
        await Run("同名同大小但内容不同不会共用会话", () => Sync(() =>
        {
            var user = NewUser(); var a = Init(user, bytes: new byte[] { 1, 2 }); var b = Init(user, bytes: new byte[] { 3, 4 }); Check(a.id != b.id);
        }));
        await Run("旧客户端无校验值时不自动续传", () => Sync(() =>
        {
            var user = NewUser();
            var a = Uploads.Init(user.Id, user.Folders[0], "", "old.txt", 2, user.Folders[0], accessPath: "__folder__/0");
            var b = Uploads.Init(user.Id, user.Folders[0], "", "old.txt", 2, user.Folders[0], accessPath: "__folder__/0");
            Check(a.ok && b.ok && a.uploadId != b.uploadId);
        }));
        await Run("完整内容校验阻止损坏文件发布", async () =>
        {
            var user = NewUser(); var item = Init(user); var wrong = new byte[item.data.Length]; await Fill(user, item.id, wrong);
            Check(!Uploads.Complete(user.Id, item.id).ok); Check(!File.Exists(Path.Combine(user.Folders[0], "file.txt")));
        });
        await Run("改为只读后拒绝后续写入并清理会话", async () =>
        {
            var user = NewUser(); var item = Init(user);
            Check(Users.UpdateUser(user.Id, null, user.Folders, true, true).success);
            using var body = new MemoryStream(item.data);
            Check(!(await Uploads.WriteChunkAsync(user.Id, item.id, 0, body, default)).ok);
            Check(!Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, ".upload-sessions"), item.id + ".part", SearchOption.AllDirectories).Any());
        });
        await Run("改为只读后拒绝完成上传", async () =>
        {
            var user = NewUser(); var item = Init(user); await Fill(user, item.id, item.data);
            Users.UpdateUser(user.Id, null, user.Folders, true, true);
            Check(!Uploads.Complete(user.Id, item.id).ok); Check(!File.Exists(Path.Combine(user.Folders[0], "file.txt")));
        });
        await Run("目录授权改变后不能写入旧目录", async () =>
        {
            var user = NewUser(); var item = Init(user); await Fill(user, item.id, item.data);
            Users.UpdateUser(user.Id, null, new List<string> { user.Folders[1] }, true, false);
            Check(!Uploads.Complete(user.Id, item.id).ok);
        });
        await Run("共享撤回后拒绝完成上传", async () =>
        {
            var owner = NewUser(); var recipient = NewUser();
            var share = Users.AddSharedFolder(recipient.Id, owner.Id, owner.Username, owner.Folders[0], "share", true).entry!;
            var data = new byte[] { 1, 2, 3 };
            var item = Uploads.Init(recipient.Id, recipient.Folders[0], "", "shared.txt", data.Length, owner.Folders[0], Hash(data), "__shared__/" + share.Id);
            Check(item.ok, item.error!); await Fill(recipient, item.uploadId!, data);
            Users.RemoveSharedFolder(recipient.Id, share.Id);
            Check(!Uploads.Complete(recipient.Id, item.uploadId!).ok);
            Check(!File.Exists(Path.Combine(owner.Folders[0], "shared.txt")));
        });
        await Run("公共目录撤回后拒绝完成上传", async () =>
        {
            var user = NewUser(); var root = user.Folders[0];
            Files.Settings.PublicShareEnabled = true; Files.Settings.PublicShareReadOnly = false; Files.Settings.PublicShareDirectories = new() { root };
            var data = new byte[] { 1, 2, 3 };
            var item = Uploads.Init("pub:192.0.2.1", "__public__", "", "public.txt", data.Length, root, Hash(data), "a");
            Check(item.ok, item.error!);
            using var body = new MemoryStream(data); Check((await Uploads.WriteChunkAsync("pub:192.0.2.1", item.uploadId!, 0, body, default)).ok);
            Files.Settings.PublicShareDirectories = new() { user.Folders[1] };
            Check(!Uploads.Complete("pub:192.0.2.1", item.uploadId!).ok);
        });
        var publicFirst = Path.Combine(Root, "public-first", "资料");
        var publicSecond = Path.Combine(Root, "public-second", "资料");
        Directory.CreateDirectory(publicFirst);
        Directory.CreateDirectory(publicSecond);
        await File.WriteAllTextAsync(Path.Combine(publicFirst, "sample.txt"), "第一份");
        await File.WriteAllTextAsync(Path.Combine(publicSecond, "sample.txt"), "第二份");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(publicFirst)!, "private.txt"), "未共享");
        Files.Settings.PublicShareDirectories = new() { publicFirst, publicSecond, publicFirst + Path.DirectorySeparatorChar };
        async Task<JsonElement[]> PublicFiles(string query = "") =>
            (await http.GetFromJsonAsync<JsonElement[]>("/api/public/files" + query))!;
        string? firstPublicPath = null;
        string? secondPublicPath = null;
        await Run("同名公共目录各有独立入口，重复路径只显示一次", async () =>
        {
            var folders = await PublicFiles();
            Check(folders.Length == 2);
            Check(folders[0].GetProperty("name").GetString() != folders[1].GetProperty("name").GetString());
            firstPublicPath = folders[0].GetProperty("path").GetString();
            secondPublicPath = folders[1].GetProperty("path").GetString();
            Check(firstPublicPath != secondPublicPath);
            foreach (var (key, content) in new[] { (firstPublicPath, "第一份"), (secondPublicPath, "第二份") })
            {
                var entries = await PublicFiles("?path=" + Uri.EscapeDataString(key!));
                var path = entries.Single().GetProperty("path").GetString()!;
                Check(await http.GetStringAsync("/api/public/files/download?path=" + Uri.EscapeDataString(path)) == content);
            }
            using var status = await http.GetAsync("/api/public/status");
            Check(!(await status.Content.ReadAsStringAsync()).Contains("public-first", StringComparison.Ordinal));
        });
        await Run("公共搜索区分同名目录且拒绝访问共享目录之外", async () =>
        {
            var search = (await http.GetFromJsonAsync<JsonElement[]>("/api/public/files/search?q=sample"))!;
            var paths = search.Select(item => item.GetProperty("path").GetString()).ToArray();
            Check(paths.Contains(firstPublicPath + "/sample.txt") && paths.Contains(secondPublicPath + "/sample.txt"));
            using var traversal = await http.GetAsync("/api/public/files/download?path=" + Uri.EscapeDataString(firstPublicPath + "/../private.txt"));
            Check(traversal.StatusCode == HttpStatusCode.NotFound);
            using var ambiguous = await http.GetAsync("/api/public/files?path=" + Uri.EscapeDataString("资料"));
            Check(ambiguous.StatusCode == HttpStatusCode.NotFound);
        });
        await Run("上传写入指定的同名目录，撤回后其入口失效", async () =>
        {
            var data = Encoding.UTF8.GetBytes("第二个目录的文件");
            using var invalid = await http.PostAsJsonAsync("/api/public/chunked/init",
                new { subPath = "不存在", fileName = "rejected.txt", fileSize = data.Length });
            Check(invalid.StatusCode == HttpStatusCode.NotFound);
            using var init = await http.PostAsJsonAsync("/api/public/chunked/init",
                new { subPath = secondPublicPath, fileName = "uploaded.txt", fileSize = data.Length, contentHash = Hash(data) });
            Check(init.IsSuccessStatusCode);
            var id = (await init.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString();
            using var write = await http.PostAsync("/api/public/chunked/write?uploadId=" + id + "&offset=0", new ByteArrayContent(data));
            Check(write.IsSuccessStatusCode);
            using var complete = await http.PostAsJsonAsync("/api/public/chunked/complete", new { uploadId = id, overwrite = true });
            Check(complete.IsSuccessStatusCode);
            Check(File.ReadAllBytes(Path.Combine(publicSecond, "uploaded.txt")).SequenceEqual(data));
            Check(!File.Exists(Path.Combine(publicFirst, "uploaded.txt")));
            Files.Settings.PublicShareDirectories = new() { publicFirst };
            Check((await PublicFiles()).Single().GetProperty("path").GetString() == firstPublicPath);
            using var removed = await http.GetAsync("/api/public/files?path=" + Uri.EscapeDataString(secondPublicPath!));
            Check(removed.StatusCode == HttpStatusCode.NotFound);
            Check((await PublicFiles("?path=" + Uri.EscapeDataString("资料"))).Length == 1);
        });
        await Run("超大分块回滚且保留已确认内容", async () =>
        {
            var user = NewUser(); var data = new byte[UploadLimits.ChunkSize + 2]; var item = Init(user, bytes: data);
            using var first = new MemoryStream(new byte[] { 0 }); Check((await Uploads.WriteChunkAsync(user.Id, item.id, 0, first, default)).ok);
            using var huge = new MemoryStream(new byte[UploadLimits.ChunkSize + 1]);
            try { await Uploads.WriteChunkAsync(user.Id, item.id, 1, huge, default); Check(false); }
            catch (InvalidDataException) { }
            var resumed = Uploads.Init(user.Id, user.Folders[0], "", "file.txt", data.Length, user.Folders[0], Hash(data), "__folder__/0");
            Check(resumed.uploadedBytes == 1);
        });
        await Run("超过声明文件大小的分块被拒绝", async () =>
        {
            var user = NewUser(); var item = Init(user, bytes: new byte[] { 1 }); using var body = new MemoryStream(new byte[] { 1, 2 });
            try { await Uploads.WriteChunkAsync(user.Id, item.id, 0, body, default); Check(false); } catch (InvalidDataException) { }
            Check(!Uploads.Complete(user.Id, item.id).ok);
        });
        await Run("负偏移和重发旧偏移不会截断文件", async () =>
        {
            var user = NewUser(); var item = Init(user); await Fill(user, item.id, item.data);
            using var body = new MemoryStream(item.data);
            Check(!(await Uploads.WriteChunkAsync(user.Id, item.id, -1, body, default)).ok);
            Check(!(await Uploads.WriteChunkAsync(user.Id, item.id, 0, body, default)).ok);
            Check(Uploads.Complete(user.Id, item.id).ok);
        });
        await Run("分块读取中断后保留已确认内容", async () =>
        {
            var user = NewUser(); var item = Init(user, bytes: new byte[131073]);
            using var first = new MemoryStream(new byte[] { 0 });
            Check((await Uploads.WriteChunkAsync(user.Id, item.id, 0, first, default)).ok);
            using var broken = new BrokenStream(new byte[131072]);
            try { await Uploads.WriteChunkAsync(user.Id, item.id, 1, broken, default); Check(false); } catch (IOException) { }
            var resumed = Uploads.Init(user.Id, user.Folders[0], "", "file.txt", item.data.Length, user.Folders[0], Hash(item.data), "__folder__/0");
            Check(resumed.uploadedBytes == 1);
        });
        await Run("并发初始化只建立一个续传会话", async () =>
        {
            var user = NewUser();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => Init(user))).ToArray();
            var sessions = await Task.WhenAll(tasks);
            Check(sessions.Select(x => x.id).Distinct().Count() == 1);
        });
        await Run("未确认覆盖时保留旧文件", async () =>
        {
            var user = NewUser(); var target = Path.Combine(user.Folders[0], "file.txt"); File.WriteAllText(target, "old");
            var item = Init(user); await Fill(user, item.id, item.data);
            Check(!Uploads.Complete(user.Id, item.id).ok); Check(File.ReadAllText(target) == "old");
            Check(Uploads.Complete(user.Id, item.id, true).ok); Check(File.ReadAllBytes(target).SequenceEqual(item.data));
        });
        await Run("目标文件被占用时保留旧文件并可重试", async () =>
        {
            var user = NewUser(); var target = Path.Combine(user.Folders[0], "file.txt"); File.WriteAllText(target, "old");
            var item = Init(user); await Fill(user, item.id, item.data);
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Uploads.Complete(user.Id, item.id, true); Check(false); } catch (IOException) { }
            }
            Check(File.ReadAllText(target) == "old"); Check(Uploads.Complete(user.Id, item.id, true).ok);
        });
        await Run("每用户待上传任务数受限", () => Sync(() =>
        {
            var user = NewUser(); var ids = new List<string>();
            for (var i = 0; i < UploadLimits.MaxSessionsPerUser; i++) ids.Add(Init(user, "q" + i).id);
            var extra = Uploads.Init(user.Id, user.Folders[0], "", "extra", 1, user.Folders[0], accessPath: "__folder__/0"); Check(!extra.ok);
            foreach (var id in ids) Uploads.CancelSession(user.Id, id);
            Check(Init(user).id.Length == 32);
        }));
        await Run("每用户待上传容量受限", () => Sync(() =>
        {
            var user = NewUser();
            var first = Uploads.Init(user.Id, user.Folders[0], "", "large", UploadLimits.MaxFileSize, user.Folders[0], accessPath: "__folder__/0"); Check(first.ok);
            var second = Uploads.Init(user.Id, user.Folders[0], "", "extra", 1, user.Folders[0], accessPath: "__folder__/0"); Check(!second.ok);
            Uploads.CancelSession(user.Id, first.uploadId!);
        }));
        await Run("过期上传会话和临时文件得到清理", () => Sync(() =>
        {
            var user = NewUser(); var item = Init(user); var path = MetaPath(item.id);
            var meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(path))!;
            meta.UpdatedAtUtc = DateTime.UtcNow.AddHours(-25); File.WriteAllText(path, JsonSerializer.Serialize(meta));
            Uploads.CleanupExpiredSessions(); Check(!File.Exists(path) && !File.Exists(meta.PartPath));
        }));
        await Run("无效上传标识不能跨目录读取会话", async () =>
        {
            var user = NewUser(); using var body = new MemoryStream(new byte[] { 1 });
            Check(!(await Uploads.WriteChunkAsync(user.Id, "../users", 0, body, default)).ok);
        });
        await Run("HTML 预览隔离脚本并保留下载内容", async () =>
        {
            var user = NewUser();
            const string fileName = "网页'样本.HTM";
            var page = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><h1>预览</h1><script>window.test=true</script>");
            await File.WriteAllBytesAsync(Path.Combine(user.Folders[0], fileName), page);
            await File.WriteAllBytesAsync(Path.Combine(publicFirst, fileName), page);

            using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "test-password" });
            Check(login.IsSuccessStatusCode);
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            var privatePath = Uri.EscapeDataString("__folder__/0/" + fileName);
            using var unauthenticated = await http.GetAsync("/api/files/preview/raw?path=" + privatePath);
            Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized);

            var publicPath = Uri.EscapeDataString(firstPublicPath + "/" + fileName);
            foreach (var (caller, prefix, path) in new[] { (client, "/api/files", privatePath), (http, "/api/public/files", publicPath) })
            {
                foreach (var route in new[] { "/preview/raw", "/preview" })
                {
                    using var preview = await caller.GetAsync(prefix + route + "?path=" + path);
                    Check(preview.IsSuccessStatusCode);
                    Check(preview.Content.Headers.ContentType?.MediaType == "text/html");
                    var policy = preview.Headers.GetValues("Content-Security-Policy").Single();
                    Check(policy.Split(';').Contains("sandbox") && policy.Contains("default-src 'none'"));
                    Check((await preview.Content.ReadAsByteArrayAsync()).SequenceEqual(page));
                }
                using var download = await caller.GetAsync(prefix + "/download?path=" + path);
                Check(download.Content.Headers.ContentDisposition?.DispositionType == "attachment");
                Check((await download.Content.ReadAsByteArrayAsync()).SequenceEqual(page));
            }
        });
        await Run("交互预览只加载授权目录内的资源", async () =>
        {
            var user = NewUser();
            const string entry = "pages/入口.html";
            foreach (var folder in new[] { user.Folders[0], publicFirst })
            {
                var pageFolder = Directory.CreateDirectory(Path.Combine(folder, "pages"));
                await File.WriteAllTextAsync(Path.Combine(folder, entry), "<!doctype html><script src=app.mjs></script>");
                await File.WriteAllTextAsync(Path.Combine(pageFolder.FullName, "app.mjs"), "export const value = 1;");
                await File.WriteAllTextAsync(Path.Combine(folder, "outside.css"), "body {}");
            }

            var privateIssue = "/api/files/preview/html?path=" + Uri.EscapeDataString("__folder__/0/" + entry);
            using var unauthorized = await http.GetAsync(privateIssue);
            Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized);
            using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "test-password" });
            Check(login.IsSuccessStatusCode);
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);

            var publicIssue = "/api/public/files/preview/html?path=" + Uri.EscapeDataString(firstPublicPath + "/" + entry);
            var privatePage = (await client.GetFromJsonAsync<JsonElement>(privateIssue)).GetProperty("url").GetString()!;
            var publicPage = (await http.GetFromJsonAsync<JsonElement>(publicIssue)).GetProperty("url").GetString()!;
            foreach (var page in new[] { privatePage, publicPage })
            {
                var assets = page[..(page.LastIndexOf('/') + 1)];
                using var frameRequest = new HttpRequestMessage(HttpMethod.Get, page);
                frameRequest.Headers.Add("Sec-Fetch-Dest", "iframe");
                using var frame = await http.SendAsync(frameRequest);
                Check(frame.IsSuccessStatusCode);
                var policy = frame.Headers.GetValues("Content-Security-Policy").Single();
                Check(policy.StartsWith("sandbox allow-scripts;") && policy.Contains(assets));
                Check(!policy.Contains("allow-same-origin"));
                var directives = policy.Split(';', StringSplitOptions.TrimEntries);
                var scripts = directives.Single(d => d.StartsWith("script-src ")).Split(' ');
                var styles = directives.Single(d => d.StartsWith("style-src ")).Split(' ');
                var fonts = directives.Single(d => d.StartsWith("font-src ")).Split(' ');
                var connections = directives.Single(d => d.StartsWith("connect-src ")).Split(' ');
                Check(scripts.Contains("https://cdn.jsdelivr.net") && scripts.Contains("https://cdnjs.cloudflare.com/ajax/libs/"));
                Check(styles.Contains("https://cdn.jsdelivr.net") && styles.Contains("https://fonts.googleapis.com"));
                Check(fonts.Contains("https://fonts.gstatic.com"));
                Check(!scripts.Contains("https:") && !styles.Contains("https:"));
                Check(connections.SequenceEqual(new[] { "connect-src", http.BaseAddress!.GetLeftPart(UriPartial.Authority) + assets }));
                using var lanFrameRequest = new HttpRequestMessage(HttpMethod.Get, page);
                lanFrameRequest.Headers.Referrer = http.BaseAddress;
                using var lanFrame = await http.SendAsync(lanFrameRequest);
                Check(lanFrame.Headers.GetValues("Content-Security-Policy").Single() == policy);
                using var direct = await http.GetAsync(page);
                Check(direct.Headers.GetValues("Content-Security-Policy").Single().StartsWith("sandbox;"));

                using var moduleRequest = new HttpRequestMessage(HttpMethod.Get, assets + "app.mjs");
                moduleRequest.Headers.Add("Origin", "null");
                using var module = await http.SendAsync(moduleRequest);
                Check(module.Content.Headers.ContentType?.MediaType == "text/javascript");
                Check(module.Headers.GetValues("Access-Control-Allow-Origin").Single() == "null");
                Check(await module.Content.ReadAsStringAsync() == "export const value = 1;");

                using var traversal = await http.GetAsync(assets + "%2e%2e%5coutside.css");
                Check(traversal.StatusCode == HttpStatusCode.NotFound);
                using var forged = await http.GetAsync("/api/html-preview/invalid/app.mjs");
                Check(forged.StatusCode == HttpStatusCode.NotFound);
            }

            using var logout = await client.PostAsync("/api/auth/logout", null);
            Check(logout.IsSuccessStatusCode);
            using var expired = await http.GetAsync(privatePage);
            Check(expired.StatusCode == HttpStatusCode.Unauthorized);

            var originalFolders = Files.Settings.PublicShareDirectories;
            try
            {
                Files.Settings.PublicShareDirectories = [publicSecond];
                using var revoked = await http.GetAsync(publicPage);
                Check(revoked.StatusCode == HttpStatusCode.NotFound);
            }
            finally { Files.Settings.PublicShareDirectories = originalFolders; }
        });
        await Run("真实接口验证上传并下载", async () =>
        {
            var user = NewUser();
            var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "test-password" }); Check(login.IsSuccessStatusCode);
            var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var data = Encoding.UTF8.GetBytes("端到端文件内容");
            var init = await client.PostAsJsonAsync("/api/chunked/init", new { subPath = "__folder__/0", fileName = "end.txt", fileSize = data.Length, contentHash = Hash(data) });
            Check(init.IsSuccessStatusCode, await init.Content.ReadAsStringAsync());
            var id = (await init.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
            Check((await http.GetAsync("/.upload-sessions/" + Path.GetFileName(Path.GetDirectoryName(MetaPath(id))) + "/" + id + ".json")).StatusCode == HttpStatusCode.NotFound);
            var write = await client.PostAsync("/api/chunked/write?uploadId=" + id + "&offset=0", new ByteArrayContent(data)); Check(write.IsSuccessStatusCode, await write.Content.ReadAsStringAsync());
            var complete = await client.PostAsJsonAsync("/api/chunked/complete", new { uploadId = id }); Check(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
            Check((await client.GetByteArrayAsync("/api/files/download?path=__folder__/0/end.txt")).SequenceEqual(data));
            var large = await client.PostAsync("/api/chunked/write?uploadId=" + id + "&offset=0", new ByteArrayContent(new byte[UploadLimits.MaxChunkRequestSize + 1]));
            Check(large.StatusCode == HttpStatusCode.RequestEntityTooLarge);

            var bounded = Init(user, "chunked-body.bin", bytes: new byte[UploadLimits.ChunkSize + 1]);
            using var unknownLength = new UnknownLengthContent(new byte[UploadLimits.ChunkSize + 1]);
            var chunked = await client.PostAsync("/api/chunked/write?uploadId=" + bounded.id + "&offset=0", unknownLength);
            Check(chunked.StatusCode == HttpStatusCode.RequestEntityTooLarge, "无长度请求未返回 413");
            var resumed = Uploads.Init(user.Id, user.Folders[0], "", "chunked-body.bin", bounded.data.Length,
                user.Folders[0], Hash(bounded.data), "__folder__/0");
            Check(resumed.uploadedBytes == 0);
        });
        await Run("真实接口拒绝中途撤回的写权限", async () =>
        {
            var user = NewUser(); var item = Init(user); var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "test-password" });
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            Users.UpdateUser(user.Id, null, user.Folders, true, true);
            var write = await client.PostAsync("/api/chunked/write?uploadId=" + item.id + "&offset=0", new ByteArrayContent(item.data));
            Check(!write.IsSuccessStatusCode); Check(!File.Exists(Path.Combine(user.Folders[0], "file.txt")));
        });

        await Run("聊天推送、发送回执和历史记录使用同一编号", async () =>
        {
            var sender = NewUser();
            var recipient = NewUser();
            using var senderClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            using var recipientClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http.BaseAddress };
            foreach (var (client, user) in new[] { (senderClient, sender), (recipientClient, recipient) })
            {
                using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "test-password" });
                Check(login.IsSuccessStatusCode);
                client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var stream = await recipientClient.GetAsync("/api/chat/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Check(stream.IsSuccessStatusCode);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));
            Check(await reader.ReadLineAsync(timeout.Token) == "event: connected");
            await reader.ReadLineAsync(timeout.Token);
            await reader.ReadLineAsync(timeout.Token);
            var ids = new List<long>();
            for (var i = 0; i < 2; i++)
            {
                using var sent = await senderClient.PostAsJsonAsync("/api/chat/send", new { to = recipient.Username, type = "text", content = "重复发送的同一段文字" }, timeout.Token);
                Check(sent.IsSuccessStatusCode);
                var receipt = await sent.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
                var line = await reader.ReadLineAsync(timeout.Token);
                Check(line?.StartsWith("data: ") == true);
                using var live = JsonDocument.Parse(line![6..]);
                await reader.ReadLineAsync(timeout.Token);
                var id = receipt.GetProperty("id").GetInt64();
                Check(id == live.RootElement.GetProperty("id").GetInt64());
                Check(receipt.GetProperty("time").GetInt64() == live.RootElement.GetProperty("time").GetInt64());
                ids.Add(id);
            }
            var history = await recipientClient.GetFromJsonAsync<JsonElement>($"/api/chat/history?with={sender.Username}&limit=50", timeout.Token);
            var historyIds = history.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("id").GetInt64());
            Check(ids.Distinct().Count() == 2 && historyIds.Order().SequenceEqual(ids.Order()));
        });
        await Run("聊天历史翻页保留同一毫秒内的独立消息", () => Sync(() =>
        {
            using var storage = new ChatStorageService(Path.Combine(Root, "chat-pagination"));
            const long time = 1700000000000;
            var first = storage.SaveMessage("sender", "receiver", "text", "相同", time: time);
            var second = storage.SaveMessage("sender", "receiver", "text", "相同", time: time);
            var latest = storage.QueryMessages("receiver", "sender", limit: 1).Single();
            Check(latest.Id == second);
            Check(storage.QueryMessages("receiver", "sender", 1, latest.CreatedAt, latest.Id).Single().Id == first);
            Check(storage.QueryMessages("receiver", "sender", 1, latest.CreatedAt).Count == 0);
        }));
        await Run("聊天搜索只匹配参与者的收发记录", () => Sync(() =>
        {
            using var storage = new ChatStorageService(Path.Combine(Root, "chat-search"));
            storage.SaveMessage("alice", "bob", "text", "查询样例，收到", time: 1);
            storage.SaveMessage("bob", "alice", "text", "查询样例，发出", time: 2);
            storage.SaveMessage("charlie", "david", "text", "查询样例，无关", time: 3);
            var matches = storage.SearchMessages(keyword: "查询样例", limit: 2, participant: "bob");
            Check(matches.Select(message => message.Content).SequenceEqual(new[] { "查询样例，发出", "查询样例，收到" }));
            Check(storage.SearchMessages(sender: "charlie", participant: "bob").Count == 0);
        }));

        await Run("搜索与文件变化并发运行", async () =>
        {
            var root = NewUser().Folders[0]; await Files.BuildIndexAsync(root);
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (var i = 0; i < 2000; i++) Files.SearchFilesWithIndex("stress", root); })).ToArray();
            for (var i = 0; i < 100; i++)
            {
                var file = Path.Combine(root, "stress" + i + ".txt"); await File.WriteAllTextAsync(file, "data");
                if (i % 2 == 0) File.Delete(file);
            }
            await Task.WhenAll(readers);
            await Eventually(() => Files.SearchFilesWithIndex("stress", root).Count == 50);
        });
        await Run("目录重命名后子文件仍可搜索", async () =>
        {
            var root = NewUser().Folders[0]; var old = Path.Combine(root, "old"); Directory.CreateDirectory(Path.Combine(old, "nested"));
            File.WriteAllText(Path.Combine(old, "nested", "needle.txt"), "data"); await Files.BuildIndexAsync(root);
            Directory.Move(old, Path.Combine(root, "new"));
            await Eventually(() => Files.SearchFilesWithIndex("needle", root).Any(x => x.Path == "new/nested/needle.txt"));
            Check(!Files.SearchFilesWithIndex("needle", root).Any(x => x.Path.StartsWith("old/")));
        });
        await Run("移入带子文件的目录后索引完整", async () =>
        {
            var root = NewUser().Folders[0]; var source = Path.Combine(Root, "incoming"); Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "incoming-needle.txt"), "data"); await Files.BuildIndexAsync(root);
            Directory.Move(source, Path.Combine(root, "incoming"));
            await Eventually(() => Files.SearchFilesWithIndex("incoming-needle", root).Count == 1);
        });
        await Run("重复构建期间的文件变化不会丢失", async () =>
        {
            var root = NewUser().Folders[0];
            for (var i = 0; i < 200; i++) File.WriteAllText(Path.Combine(root, "seed" + i), "data");
            var builds = Enumerable.Range(0, 8).Select(_ => Files.BuildIndexAsync(root)).ToArray();
            File.WriteAllText(Path.Combine(root, "final-needle"), "data"); await Task.WhenAll(builds);
            await Eventually(() => Files.SearchFilesWithIndex("final-needle", root).Count == 1);
        });
        await app.StopAsync();
        var report = JsonSerializer.Serialize(new { passed = Results.Count - _failed, failed = _failed, results = Results }, new JsonSerializerOptions { WriteIndented = true });
        if (reportPath != null) File.WriteAllText(reportPath, report);
        Console.WriteLine($"通过 {Results.Count - _failed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    private sealed class BrokenStream(byte[] data) : MemoryStream(data)
    {
        private int _reads;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (++_reads > 1) throw new IOException("模拟连接中断");
            return base.ReadAsync(buffer, offset, count, ct);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);
    }
}
