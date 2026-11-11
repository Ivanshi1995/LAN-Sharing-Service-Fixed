using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace LanFileShare;

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
}

public class UserBandwidthDto
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsBlacklisted { get; set; }
    public double UpSpeed { get; set; }
    public double DownSpeed { get; set; }
    public string UpSpeedDisplay => FormatBandwidthSpeed(UpSpeed);
    public string DownSpeedDisplay => FormatBandwidthSpeed(DownSpeed);
    public double UpLimit { get; set; }
    public double DownLimit { get; set; }
    public DateTime LastSeen { get; set; }

    private static string FormatBandwidthSpeed(double bps)
    {
        if (bps < 1024) return $"{bps:0.#} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0.#} KB/s";
        if (bps < 1024 * 1024 * 1024) return $"{bps / (1024 * 1024):0.0} MB/s";
        return $"{bps / (1024 * 1024 * 1024):0.0} GB/s";
    }
}

public class UserSpeedTracker
{
    public string Username { get; set; } = "";
    public string Ip { get; set; } = "";
    public long TotalUploaded;
    public long TotalDownloaded;
    public long LastUploadBytes;
    public long LastDownloadBytes;
    public DateTime LastSpeedCheck { get; set; } = DateTime.UtcNow;
    public double CurrentUploadSpeed { get; set; }
    public double CurrentDownloadSpeed { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public class BandwidthLimitEntry
{
    public double UpLimitMbps { get; set; }
    public double DownLimitMbps { get; set; }
}

public class FileItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string SizeDisplay => FormatSize(Size);
    public DateTime LastModified { get; set; }
    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");
    public string Icon => IsDirectory ? "mdi:folder" : GetFileIcon(Name);
    public string IconColor => IsDirectory ? "text-yellow-500 text-2xl" : GetFileIconColor(Name);
    public bool IsShared { get; set; } = false;
    // 多目录共享根切换的虚拟条目
    public bool IsOwnRoot { get; set; } = false;
    public string? ParentPath { get; set; }
    public string? SharedSourceName { get; set; }
    public List<FileItem>? Children { get; set; }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "--";
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {sizes[order]}";
    }

    private static string GetFileIcon(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLower();
        return ext switch
        {
            ".pdf" => "mdi:file-pdf-box",
            ".doc" or ".docx" => "mdi:file-word-box",
            ".xls" or ".xlsx" => "mdi:file-excel-box",
            ".ppt" or ".pptx" => "mdi:file-powerpoint-box",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "mdi:file-image-outline",
            ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" => "mdi:file-video-outline",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "mdi:file-music-outline",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "mdi:folder-zip-outline",
            ".md" => "mdi:language-markdown",
            ".cs" or ".py" or ".js" or ".ts" or ".java" or ".go" or ".rs" or ".cpp" or ".c" => "mdi:code-tags",
            ".txt" => "mdi:file-document-outline",
            ".html" or ".htm" or ".css" => "mdi:language-html5",
            ".json" or ".xml" or ".yaml" or ".yml" => "mdi:code-json",
            _ => "mdi:file-outline"
        };
    }

    private static string GetFileIconColor(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLower();
        return ext switch
        {
            ".pdf" => "text-red-500 text-xl",
            ".doc" or ".docx" => "text-blue-500 text-xl",
            ".xls" or ".xlsx" => "text-green-600 text-xl",
            ".ppt" or ".pptx" => "text-orange-500 text-xl",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "text-purple-500 text-xl",
            ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" => "text-blue-400 text-xl",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "text-pink-500 text-xl",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "text-amber-600 text-xl",
            ".md" => "text-blue-400 text-2xl",
            ".cs" or ".py" or ".js" or ".ts" or ".java" or ".go" or ".rs" or ".cpp" or ".c" => "text-slate-400 text-xl",
            _ => "text-slate-400 text-2xl"
        };
    }
}

public class FileShareSettings
{
    public string UploadDirectory { get; set; } = "./SharedFiles";
    public int Port { get; set; } = 8080;
    public bool AutoStart { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public string SelectedInterface { get; set; } = "";
    public bool PublicShareEnabled { get; set; }
    public bool PublicShareReadOnly { get; set; } = true;
    public List<string> PublicShareDirectories { get; set; } = new List<string>();
    // 兼容旧版单目录配置
    public string PublicShareDirectory
    {
        get => PublicShareDirectories.Count > 0 ? PublicShareDirectories[0] : "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !PublicShareDirectories.Contains(value))
                PublicShareDirectories.Add(value);
        }
    }
    public double WebViewCacheLimitGB { get; set; } = 1.0;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
}

public class FileShareService
{
    private readonly string _settingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");
    private readonly ILogger<FileShareService> _logger;
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly object _settingsLock = new();
    private FileShareSettings _settings;
    private long _totalBytesUploaded;
    private long _totalBytesDownloaded;
    private DateTime _lastSpeedCheck = DateTime.UtcNow;
    private long _lastUploadBytes;
    private long _lastDownloadBytes;
    private double _currentUploadSpeed;
    private double _currentDownloadSpeed;
    private readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();
    private CancellationTokenSource? _heartbeatCts;

    // 文件索引缓存（按基目录分片，加速搜索）
    private readonly ConcurrentDictionary<string, FileIndexState> _fileIndices = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new();

    // 每用户速度追踪
    private readonly ConcurrentDictionary<string, UserSpeedTracker> _userSpeedTrackers = new();
    // IP 黑名单
    private readonly ConcurrentDictionary<string, DateTime> _blacklistIPs = new();
    private readonly string _blacklistPath = Path.Combine(AppPaths.DataDirectory, "blacklist.json");
    // 每用户带宽限速 (Mbps)
    private readonly ConcurrentDictionary<string, BandwidthLimitEntry> _bandwidthLimits = new();
    private readonly string _bandwidthLimitPath = Path.Combine(AppPaths.DataDirectory, "bandwidth_limits.json");

    public event Action<LogEntry>? OnLogAdded;

    public FileShareService(ILogger<FileShareService> logger)
    {
        _logger = logger;
        _settings = LoadSettings();
        LoadBlacklist();
        LoadBandwidthLimits();
        EnsureUploadDirectory();
    }

    public FileShareSettings Settings => _settings;

    public void AddLog(string level, string message) => AddLog(level, null, message);

    public void AddLog(string level, string? source, string message)
    {
        var fullMessage = string.IsNullOrEmpty(source) ? message : $"[{source}] {message}";
        var entry = new LogEntry { Time = DateTime.Now, Level = level, Message = fullMessage };
        _logs.Enqueue(entry);

        while (_logs.Count > 500)
            _logs.TryDequeue(out _);

        WriteLogFile(entry);
        OnLogAdded?.Invoke(entry);
    }

    public List<LogEntry> GetLogs()
    {
        return _logs.Reverse().ToList();
    }

    public void ClearLogs()
    {
        _logs.Clear();
        AddLog("INFO", "服务端", "运行日志已清除");
    }

    public List<LogEntry> GetRecentLogs(int count = 50)
    {
        return _logs.Reverse().Take(count).ToList();
    }

    public void RecordHeartbeat(string ip)
    {
        var isNew = !_heartbeats.ContainsKey(ip);
        _heartbeats[ip] = DateTime.UtcNow;
        if (isNew) AddLog("CONN", "客户端", $"设备上线: {ip}");
    }

    public int ActiveConnections => _heartbeats.Count;
    public int UniqueDeviceCount
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-15);
            return _heartbeats.Count(kv => kv.Value >= cutoff);
        }
    }

    public void StartHeartbeatCleanup()
    {
        _heartbeatCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_heartbeatCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _heartbeatCts.Token);
                    var cutoff = DateTime.UtcNow.AddSeconds(-75);
                    foreach (var kv in _heartbeats)
                    {
                        if (kv.Value < cutoff)
                            _heartbeats.TryRemove(kv.Key, out _);
                    }
                    OnHeartbeatCleanup?.Invoke();
                }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public Action? OnHeartbeatCleanup { get; set; }

    public void StopHeartbeatCleanup()
    {
        _heartbeatCts?.Cancel();
    }

    public void TrackUploaded(long bytes)
    {
        Interlocked.Add(ref _totalBytesUploaded, bytes);
    }

    public void TrackDownloaded(long bytes)
    {
        Interlocked.Add(ref _totalBytesDownloaded, bytes);
    }

    public (double uploadSpeed, double downloadSpeed, long totalUploaded, long totalDownloaded) GetStats()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheck).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var currentUpload = Interlocked.Read(ref _totalBytesUploaded);
            var currentDownload = Interlocked.Read(ref _totalBytesDownloaded);
            _currentUploadSpeed = (currentUpload - _lastUploadBytes) / elapsed;
            _currentDownloadSpeed = (currentDownload - _lastDownloadBytes) / elapsed;
            _lastUploadBytes = currentUpload;
            _lastDownloadBytes = currentDownload;
            _lastSpeedCheck = now;
        }
        return (_currentUploadSpeed, _currentDownloadSpeed, Interlocked.Read(ref _totalBytesUploaded), Interlocked.Read(ref _totalBytesDownloaded));
    }

    public void SaveSettings(FileShareSettings newSettings)
    {
        lock (_settingsLock)
        {
            _settings = newSettings;
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);

            // Handle auto-start registry
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    var exePath = Environment.ProcessPath ?? "";

                    if (_settings.AutoStart && !string.IsNullOrEmpty(exePath))
                        key?.SetValue("LanFileShare", $"\"{exePath}\"");
                    else
                        key?.DeleteValue("LanFileShare", false);
                }
                catch { /* ignore registry errors */ }
            }

            EnsureUploadDirectory();
        }
    }

    public string GetServerUrl(string host, int port)
    {
        return $"http://{host}:{port}";
    }

    public string GetClientUrl(string host, int port)
    {
        return $"http://{host}:{port}";
    }

    public string GetPublicShareUrl(string host, int port)
    {
        return $"{GetServerUrl(host, port)}/public";
    }

    public string GetPublicSharePath()
    {
        var dirs = GetPublicSharePaths();
        return dirs.Count > 0 ? dirs[0] : Path.GetFullPath("./PublicShare");
    }

    public List<string> GetPublicSharePaths()
    {
        var result = new List<string>();
        foreach (var dir in _settings.PublicShareDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                try { result.Add(Path.GetFullPath(dir)); }
                catch { }
            }
        }
        return result;
    }

    public void EnsurePublicShareDirectories()
    {
        foreach (var dir in GetPublicSharePaths())
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AddLog("INFO", "服务端", $"创建公共共享目录: {dir}");
            }
        }
    }

    public void EnsurePublicShareDirectory()
    {
        EnsurePublicShareDirectories();
    }

    public static string NormalizeSubPath(string subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath)) return "";
        return subPath.Replace('\\', '/').Trim('/').Replace('/', Path.DirectorySeparatorChar);
    }

    public static bool IsPathUnderDirectory(string baseDirectory, string candidatePath)
    {
        try
        {
            var baseFull = Path.GetFullPath(baseDirectory);
            if (!baseFull.EndsWith(Path.DirectorySeparatorChar))
                baseFull += Path.DirectorySeparatorChar;
            var targetFull = Path.GetFullPath(candidatePath);
            return targetFull.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetFull.TrimEnd(Path.DirectorySeparatorChar), baseFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public List<FileItem> ListFiles(string subPath = "", string? baseDirectory = null)
    {
        var baseDir = ResolveBaseDirectory(baseDirectory);
        subPath = NormalizeSubPath(subPath);
        var targetDir = string.IsNullOrEmpty(subPath) ? baseDir : Path.Combine(baseDir, subPath);

        if (!IsPathUnderDirectory(baseDir, targetDir))
            return new List<FileItem>();

        if (!Directory.Exists(targetDir))
            return new List<FileItem>();

        var items = new List<FileItem>();

        // Directories first
        foreach (var dir in Directory.GetDirectories(targetDir))
        {
            var dirInfo = new DirectoryInfo(dir);
            if ((dirInfo.Attributes & FileAttributes.Hidden) != 0) continue;

            var relPath = Path.GetRelativePath(baseDir, dir).Replace('\\', '/');
            items.Add(new FileItem
            {
                Name = dirInfo.Name,
                Path = relPath,
                IsDirectory = true,
                Size = -1,
                LastModified = dirInfo.LastWriteTime
            });
        }

        // Then files
        foreach (var file in Directory.GetFiles(targetDir))
        {
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.Hidden) != 0) continue;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            items.Add(new FileItem
            {
                Name = fileInfo.Name,
                Path = relPath,
                IsDirectory = false,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime
            });
        }

        return items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 递归搜索，按名称模糊匹配
    public List<FileItem> SearchFiles(string query, string? baseDirectory = null, int maxResults = 200)
    {
        var baseDir = ResolveBaseDirectory(baseDirectory);
        if (!Directory.Exists(baseDir))
            return new List<FileItem>();

        var results = new List<FileItem>();

        // 搜索所有匹配目录
        foreach (var dir in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories))
        {
            if (results.Count >= maxResults) break;
            var dirInfo = new DirectoryInfo(dir);
            if ((dirInfo.Attributes & FileAttributes.Hidden) != 0) continue;
            if (!dirInfo.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var relPath = Path.GetRelativePath(baseDir, dir).Replace('\\', '/');
            var parentDir = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
            results.Add(new FileItem
            {
                Name = dirInfo.Name,
                Path = relPath,
                IsDirectory = true,
                Size = -1,
                LastModified = dirInfo.LastWriteTime,
                ParentPath = parentDir
            });
        }

        // 搜索所有匹配文件
        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            if (results.Count >= maxResults) break;
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.Hidden) != 0) continue;
            if (!fileInfo.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            var relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
            var parentDir = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
            results.Add(new FileItem
            {
                Name = fileInfo.Name,
                Path = relPath,
                IsDirectory = false,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime,
                ParentPath = parentDir
            });
        }

        return results.OrderBy(i => i.IsDirectory ? 0 : 1)
                      .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                      .Take(maxResults)
                      .ToList();
    }

    public string? GetFullPath(string relativePath, string? baseDirectory = null)
    {
        var baseDir = ResolveBaseDirectory(baseDirectory);
        relativePath = Uri.UnescapeDataString(relativePath);
        relativePath = NormalizeSubPath(relativePath);
        var fullPath = string.IsNullOrEmpty(relativePath)
            ? baseDir
            : Path.GetFullPath(Path.Combine(baseDir, relativePath));

        if (!IsPathUnderDirectory(baseDir, fullPath))
            return null;

        return fullPath;
    }

    public void EnsureUploadDirectory()
    {
        var dir = GetFullUploadPath();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            AddLog("INFO", "服务端", $"创建目录: {dir}");
        }
    }

    public string GetFullUploadPath()
    {
        var dir = string.IsNullOrWhiteSpace(_settings.UploadDirectory) ? "./SharedFiles" : _settings.UploadDirectory;
        try
        {
            return Path.GetFullPath(dir);
        }
        catch
        {
            return Path.GetFullPath("./SharedFiles");
        }
    }

    public string ResolveBaseDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return GetFullUploadPath();
        try { return Path.GetFullPath(baseDirectory); }
        catch { return GetFullUploadPath(); }
    }

    public List<NetworkInterfaceInfo> GetNetworkInterfaces()
    {
        var list = new List<NetworkInterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    list.Add(new NetworkInterfaceInfo
                    {
                        IP = addr.Address.ToString(),
                        Name = nic.Name,
                        Description = nic.Description
                    });
                }
            }
        }

        // Add loopback if not present
        if (!list.Any(i => i.IP == "127.0.0.1"))
        {
            list.Insert(0, new NetworkInterfaceInfo
            {
                IP = "127.0.0.1",
                Name = "Loopback",
                Description = "Loopback"
            });
        }

        return list;
    }

    public string GetPrimaryIP()
    {
        var interfaces = GetNetworkInterfaces();
        if (_settings.SelectedInterface != "" &&
            interfaces.Any(i => i.IP == _settings.SelectedInterface))
        {
            return _settings.SelectedInterface;
        }
        return GetDefaultLanIP();
    }

    public string GetDefaultLanIP()
    {
        var interfaces = GetNetworkInterfaces();
        return interfaces.FirstOrDefault(i => i.IP != "127.0.0.1")?.IP
            ?? interfaces.FirstOrDefault()?.IP
            ?? "127.0.0.1";
    }

    private static readonly object _logFileLock = new();

    private static void WriteLogFile(LogEntry entry)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "log");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, entry.Time.ToString("yyyyMMdd") + ".log");
            lock (_logFileLock)
                File.AppendAllText(file, $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
        }
        catch { }
    }

    // ===== 每用户速度追踪 =====

    public void TrackUserUploaded(string userId, string username, string ip, long bytes)
    {
        var tracker = _userSpeedTrackers.GetOrAdd(userId, _ => new UserSpeedTracker());
        tracker.Username = username;
        tracker.Ip = ip;
        Interlocked.Add(ref tracker.TotalUploaded, bytes);
        tracker.LastSeen = DateTime.UtcNow;
    }

    public void TrackUserDownloaded(string userId, string username, string ip, long bytes)
    {
        var tracker = _userSpeedTrackers.GetOrAdd(userId, _ => new UserSpeedTracker());
        tracker.Username = username;
        tracker.Ip = ip;
        Interlocked.Add(ref tracker.TotalDownloaded, bytes);
        tracker.LastSeen = DateTime.UtcNow;
    }

    // 每 chunk 调用一次，实时速度追踪用
    public void TrackUserDownloadBytes(string userId, long bytes)
    {
        if (bytes <= 0) return;
        var tracker = _userSpeedTrackers.GetOrAdd(userId, _ => new UserSpeedTracker());
        Interlocked.Add(ref tracker.TotalDownloaded, bytes);
        tracker.LastSeen = DateTime.UtcNow;
    }

    // 每 chunk 调用一次，实时速度追踪用
    public void TrackUserUploadBytes(string userId, long bytes)
    {
        if (bytes <= 0) return;
        var tracker = _userSpeedTrackers.GetOrAdd(userId, _ => new UserSpeedTracker());
        Interlocked.Add(ref tracker.TotalUploaded, bytes);
        tracker.LastSeen = DateTime.UtcNow;
        Interlocked.Add(ref _totalBytesUploaded, bytes);
    }

    public void UpdateUserOnline(string userId, string username, string ip)
    {
        var tracker = _userSpeedTrackers.GetOrAdd(userId, _ => new UserSpeedTracker());
        tracker.Username = username;
        tracker.Ip = ip;
        tracker.LastSeen = DateTime.UtcNow;
    }

    public List<UserBandwidthDto> GetBandwidthUsers(List<OnlineUserDto> onlineUsers)
    {
        var now = DateTime.UtcNow;

        // 计算瞬时速度
        foreach (var (userId, tracker) in _userSpeedTrackers)
        {
            var elapsed = (now - tracker.LastSpeedCheck).TotalSeconds;
            if (elapsed >= 1.0)
            {
                var currentUp = Interlocked.Read(ref tracker.TotalUploaded);
                var currentDown = Interlocked.Read(ref tracker.TotalDownloaded);
                tracker.CurrentUploadSpeed = elapsed > 0 ? (currentUp - tracker.LastUploadBytes) / elapsed : 0;
                tracker.CurrentDownloadSpeed = elapsed > 0 ? (currentDown - tracker.LastDownloadBytes) / elapsed : 0;
                tracker.LastUploadBytes = currentUp;
                tracker.LastDownloadBytes = currentDown;
                tracker.LastSpeedCheck = now;
            }
            // 速度衰减：超过 10 秒无新数据则归零
            if ((now - tracker.LastSeen).TotalSeconds > 10)
            {
                tracker.CurrentUploadSpeed = 0;
                tracker.CurrentDownloadSpeed = 0;
            }
        }

        var result = new List<UserBandwidthDto>();

        // 先添加在线用户
        foreach (var online in onlineUsers)
        {
            var tracker = _userSpeedTrackers.Values
                .FirstOrDefault(t => string.Equals(t.Username, online.Username, StringComparison.OrdinalIgnoreCase));
            var userId = _userSpeedTrackers
                .FirstOrDefault(kv => string.Equals(kv.Value.Username, online.Username, StringComparison.OrdinalIgnoreCase)).Key ?? "";

            var isBlacklisted = _blacklistIPs.ContainsKey(online.Ip);
            var limits = _bandwidthLimits.GetValueOrDefault(userId) ?? new BandwidthLimitEntry();
            result.Add(new UserBandwidthDto
            {
                UserId = userId,
                Username = online.Username,
                Ip = online.Ip,
                IsOnline = true,
                IsBlacklisted = isBlacklisted,
                UpSpeed = tracker?.CurrentUploadSpeed ?? 0,
                DownSpeed = tracker?.CurrentDownloadSpeed ?? 0,
                UpLimit = limits.UpLimitMbps,
                DownLimit = limits.DownLimitMbps,
                LastSeen = tracker?.LastSeen ?? now
            });
        }

        // 添加公共共享用户和离线但最近活跃的用户
        foreach (var (userId, tracker) in _userSpeedTrackers)
        {
            if (result.Any(r => string.Equals(r.Username, tracker.Username, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.Ip, tracker.Ip, StringComparison.OrdinalIgnoreCase)))
                continue;

            var isRecent = (now - tracker.LastSeen).TotalSeconds <= 15;
            var isPublic = userId.StartsWith("pub:") || userId.StartsWith("pub_");
            var isBlacklisted = !string.IsNullOrEmpty(tracker.Ip) && _blacklistIPs.ContainsKey(tracker.Ip);
            var limits = _bandwidthLimits.GetValueOrDefault(userId) ?? new BandwidthLimitEntry();

            result.Add(new UserBandwidthDto
            {
                UserId = userId,
                Username = isPublic ? $"公共用户 ({tracker.Ip})" : tracker.Username,
                Ip = tracker.Ip,
                IsOnline = isPublic ? isRecent : false,
                IsBlacklisted = isBlacklisted,
                UpSpeed = isRecent ? tracker.CurrentUploadSpeed : 0,
                DownSpeed = isRecent ? tracker.CurrentDownloadSpeed : 0,
                UpLimit = limits.UpLimitMbps,
                DownLimit = limits.DownLimitMbps,
                LastSeen = tracker.LastSeen
            });
        }

        return result;
    }

    // ===== IP 黑名单管理 =====

    public bool IsIPBlacklisted(string ip)
    {
        return _blacklistIPs.ContainsKey(ip);
    }

    public void BanIP(string ip)
    {
        _blacklistIPs[ip] = DateTime.UtcNow;
        SaveBlacklist();
        AddLog("INFO", "黑名单", $"封禁 IP: {ip}");
    }

    public void UnbanIP(string ip)
    {
        _blacklistIPs.TryRemove(ip, out _);
        SaveBlacklist();
        AddLog("INFO", "黑名单", $"解封 IP: {ip}");
    }

    public List<string> GetBlacklistedIPs()
    {
        return _blacklistIPs.Keys.OrderBy(k => k).ToList();
    }

    // ===== 带宽限速管理 =====

    public void SetBandwidthLimit(string userId, double upLimitMbps, double downLimitMbps)
    {
        _bandwidthLimits[userId] = new BandwidthLimitEntry
        {
            UpLimitMbps = Math.Max(0, upLimitMbps),
            DownLimitMbps = Math.Max(0, downLimitMbps)
        };
        SaveBandwidthLimits();
    }

    public (double upLimit, double downLimit) GetBandwidthLimit(string userId)
    {
        if (_bandwidthLimits.TryGetValue(userId, out var entry))
            return (entry.UpLimitMbps, entry.DownLimitMbps);
        return (0, 0);
    }

    // ===== 持久化 =====

    private void LoadBlacklist()
    {
        try
        {
            if (File.Exists(_blacklistPath))
            {
                var json = File.ReadAllText(_blacklistPath);
                var list = JsonSerializer.Deserialize<List<BlacklistEntry>>(json) ?? new();
                foreach (var entry in list)
                    _blacklistIPs[entry.Ip] = entry.BanTime;
            }
        }
        catch { }
    }

    private void SaveBlacklist()
    {
        try
        {
            var list = _blacklistIPs.Select(kv => new BlacklistEntry { Ip = kv.Key, BanTime = kv.Value }).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_blacklistPath, json);
        }
        catch { }
    }

    private void LoadBandwidthLimits()
    {
        try
        {
            if (File.Exists(_bandwidthLimitPath))
            {
                var json = File.ReadAllText(_bandwidthLimitPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, BandwidthLimitEntry>>(json) ?? new();
                foreach (var (userId, entry) in dict)
                    _bandwidthLimits[userId] = entry;
            }
        }
        catch { }
    }

    private void SaveBandwidthLimits()
    {
        try
        {
            var dict = _bandwidthLimits.ToDictionary(kv => kv.Key, kv => kv.Value);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_bandwidthLimitPath, json);
        }
        catch { }
    }

    private FileShareSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<FileShareSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new FileShareSettings();
            }
        }
        catch { }
        return new FileShareSettings();
    }

    // ============ 文件索引缓存（加速搜索）============

    // 获取索引，没有就后台构建
    private FileIndexState? GetOrRequestIndex(string baseDir)
    {
        var key = NormalizeIndexKey(baseDir);
        if (_fileIndices.TryGetValue(key, out var state) && state.Ready)
            return state;

        // 首次请求：启动后台构建
         _ = BuildIndexAsync(baseDir);
        return null;
    }

    // 内存索引搜索，不碰磁盘
    public List<FileItem> SearchFilesWithIndex(string query, string baseDir, int maxResults = 200)
    {
        var state = GetOrRequestIndex(baseDir);
        if (state == null)
        {
            // 索引尚未就绪，回退到磁盘搜索
            AddLog("WARN", "INDEX", $"索引未就绪，使用磁盘搜索: {baseDir}");
            return SearchFiles(query, baseDir, maxResults);
        }

        var results = new List<FileItem>();
        var entries = state.Entries;

        // 先搜目录
        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) break;
            if (!entry.IsDirectory) continue;
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new FileItem
            {
                Name = entry.Name,
                Path = entry.Path,
                IsDirectory = true,
                Size = -1,
                LastModified = entry.LastModified,
                ParentPath = entry.ParentPath
            });
        }

        // 再搜文件
        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) break;
            if (entry.IsDirectory) continue;
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            results.Add(new FileItem
            {
                Name = entry.Name,
                Path = entry.Path,
                IsDirectory = false,
                Size = entry.Size,
                LastModified = entry.LastModified,
                ParentPath = entry.ParentPath
            });
        }

        return results;
    }

    // A single state owns each root. Watch before scanning to capture changes during the build.
    public async Task BuildIndexAsync(string baseDir)
    {
        var key = NormalizeIndexKey(baseDir);
        var state = _fileIndices.GetOrAdd(key, _ => new FileIndexState { Key = key, RootPath = Path.GetFullPath(baseDir) });
        lock (state.Sync)
        {
            if (state.Building) { state.RebuildRequested = true; return; }
            state.Building = true;
            SetupFileWatcher(baseDir);
        }
        try
        {
            while (true)
            {
                lock (state.Sync) state.RebuildRequested = false;
                var entries = await Task.Run(() => ScanIndex(baseDir));
                lock (state.Sync)
                {
                    state.Entries = entries;
                    state.Ready = true;
                    state.BuildTime = DateTime.UtcNow;
                    if (state.RebuildRequested) continue;
                    state.Building = false;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            lock (state.Sync) { state.Building = false; state.Ready = false; }
            AddLog("ERROR", "INDEX", $"构建索引失败: {ex.Message}");
        }
    }

    private static FileIndexEntry[] ScanIndex(string baseDir)
    {
        if (!Directory.Exists(baseDir)) return Array.Empty<FileIndexEntry>();
        var list = new List<FileIndexEntry>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFileSystemEntries(baseDir, "*", options))
        {
            try
            {
                var entry = ReadIndexEntry(baseDir, path);
                if (entry != null) list.Add(entry);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.ToArray();
    }

    private static FileIndexEntry? ReadIndexEntry(string baseDir, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return null;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var relative = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
        return new FileIndexEntry
        {
            Name = Path.GetFileName(path), Path = relative, IsDirectory = isDirectory,
            Size = isDirectory ? -1 : new FileInfo(path).Length,
            LastModified = File.GetLastWriteTime(path),
            ParentPath = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? ""
        };
    }

    private void SetupFileWatcher(string baseDir)
    {
        var key = NormalizeIndexKey(baseDir);
        if (_fileWatchers.ContainsKey(key) || !Directory.Exists(baseDir)) return;
        try
        {
            var watcher = new FileSystemWatcher(baseDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 65536
            };
            watcher.Created += (_, e) => OnFileChanged(baseDir, e.FullPath, e.ChangeType);
            watcher.Deleted += (_, e) => OnFileChanged(baseDir, e.FullPath, e.ChangeType);
            watcher.Changed += (_, e) => OnFileChanged(baseDir, e.FullPath, e.ChangeType);
            watcher.Renamed += (_, _) => { _ = BuildIndexAsync(baseDir); };
            watcher.Error += (_, _) => { _ = BuildIndexAsync(baseDir); };
            _fileWatchers[key] = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { AddLog("WARN", "INDEX", $"无法启动文件监控: {ex.Message}"); }
    }

    private void OnFileChanged(string baseDir, string fullPath, WatcherChangeTypes changeType)
    {
        if (!_fileIndices.TryGetValue(NormalizeIndexKey(baseDir), out var state)) return;
        var rebuild = false;
        lock (state.Sync)
        {
            if (state.Building) { state.RebuildRequested = true; return; }
            if (!state.Ready) { rebuild = true; }
            else
            {
                try
                {
                    var relative = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
                    if (Directory.Exists(fullPath)) rebuild = true; // Includes populated directory moves.
                    else
                    {
                        // Publish a new snapshot. Readers never enumerate a mutable collection.
                        var remaining = state.Entries.Where(e =>
                            !string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase)
                            && !e.Path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                        var entry = ReadIndexEntry(baseDir, fullPath);
                        if (entry != null) remaining.Add(entry);
                        state.Entries = remaining.ToArray();
                    }
                }
                catch (IOException) { rebuild = true; }
                catch (UnauthorizedAccessException) { rebuild = true; }
            }
        }
        if (rebuild) _ = BuildIndexAsync(baseDir);
    }

    // 索引键标准化
    private static string NormalizeIndexKey(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }

    // Keep the state and watcher paired while rebuilding.
    public void RebuildAllIndices()
    {
        foreach (var state in _fileIndices.Values) _ = BuildIndexAsync(state.RootPath);
    }

}

// 索引条目
internal class FileIndexEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public string ParentPath { get; init; } = "";
}

// 目录索引状态
internal class FileIndexState
{
    public string Key { get; set; } = "";
    public string RootPath { get; set; } = "";
    public readonly object Sync = new();
    public volatile FileIndexEntry[] Entries = Array.Empty<FileIndexEntry>();
    public volatile bool Ready;
    public bool Building;
    public bool RebuildRequested;
    public DateTime BuildTime { get; set; }
}

public class BlacklistEntry
{
    public string Ip { get; set; } = "";
    public DateTime BanTime { get; set; }
}

public class NetworkInterfaceInfo
{
    public string IP { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
