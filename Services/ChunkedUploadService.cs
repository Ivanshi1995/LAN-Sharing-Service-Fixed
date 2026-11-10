using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanFileShare;

public static class UploadLimits
{
    public const long MaxFileSize = 200L * 1024 * 1024 * 1024;
    public const int ChunkSize = 5 * 1024 * 1024;
    public const int MaxChunkRequestSize = ChunkSize + 1024 * 1024;
    public const long ReservedDiskBytes = 256L * 1024 * 1024;
    public const int MaxSessionsPerUser = 32;
    public const int MaxSessionsTotal = 256;
    public const long MaxPendingBytesPerUser = MaxFileSize;
    public const long MaxPendingBytesTotal = 1024L * 1024 * 1024 * 1024;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
}

public class UploadSessionMeta
{
    public int Version { get; set; }
    public string UploadId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserFolder { get; set; } = "";
    public string SubPath { get; set; } = "";
    public string SafeRelativePath { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string AccessPath { get; set; } = "";
    public long FileSize { get; set; }
    public string PartPath { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    // 预解析的基础目录（共享文件夹上传时用，非空就跳过 UserFolder 解析）
    public string? ResolvedBaseDir { get; set; }
}

public class ChunkedUploadService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly string _sessionsRoot;
    private readonly FileShareService _service;
    private readonly UploadAccessPolicy _access;
    // Fixed lock stripes avoid retaining one semaphore for every historical upload.
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly object _allocationLock = new();
    private readonly System.Threading.Timer _cleanup;

    public ChunkedUploadService(FileShareService service, UploadAccessPolicy access)
    {
        _service = service;
        _access = access;
        _sessionsRoot = Path.Combine(AppPaths.DataDirectory, ".upload-sessions");
        Directory.CreateDirectory(_sessionsRoot);
        CleanupExpiredSessions();
        _cleanup = new System.Threading.Timer(_ => CleanupExpiredSessions(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public (bool ok, string? error, string? uploadId, long uploadedBytes, int chunkSize, bool fileExists) Init(
        string userId, string userFolder, string subPath, string fileName, long fileSize, string? resolvedBaseDir = null, string? contentHash = null, string accessPath = "")
    {
        var gate = GetGate(userId);
        gate.Wait();
        try
        {
            if (fileSize <= 0) return (false, "无效的文件大小", null, 0, UploadLimits.ChunkSize, false);
            if (fileSize > UploadLimits.MaxFileSize)
                return (false, "单文件不能超过 200GB", null, 0, UploadLimits.ChunkSize, false);

            contentHash = contentHash?.ToLowerInvariant() ?? "";
            if (contentHash.Length != 0 && (contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit)))
                return (false, "文件校验值无效。请重新选择文件。", null, 0, UploadLimits.ChunkSize, false);

            subPath = FileShareService.NormalizeSubPath(subPath);
            var safeRelativePath = SanitizeRelativePath(fileName.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(safeRelativePath))
                return (false, "无效的文件名", null, 0, UploadLimits.ChunkSize, false);

            var baseDir = resolvedBaseDir ?? (userFolder == "__public__"
                ? _service.GetPublicSharePath()
                : _service.ResolveBaseDirectory(userFolder));
            var targetDir = string.IsNullOrEmpty(subPath) ? baseDir : Path.Combine(baseDir, subPath);
            var destPath = Path.Combine(targetDir, safeRelativePath);
            if (!FileShareService.IsPathUnderDirectory(baseDir, targetDir)
                || !FileShareService.IsPathUnderDirectory(baseDir, destPath))
                return (false, "无效的上传路径", null, 0, UploadLimits.ChunkSize, false);

            var fileExists = File.Exists(destPath);

            var sessionKey = BuildSessionKey(userId, Path.GetFullPath(destPath).ToUpperInvariant(), fileSize, contentHash, accessPath);
            var userDir = GetUserDir(userId);
            Directory.CreateDirectory(userDir);

            var existing = contentHash.Length == 64 ? FindExistingSession(userDir, sessionKey) : null;
            if (existing != null)
            {
                if (!_access.CanWrite(existing))
                {
                    DeleteSession(existing, userDir);
                    return (false, "上传权限已失效。请重新选择目录。", null, 0, UploadLimits.ChunkSize, fileExists);
                }
                var uploaded = GetPartLength(existing.PartPath);
                if (uploaded > fileSize) uploaded = 0;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                SaveMeta(existing, userDir);
                return (true, null, existing.UploadId, uploaded, UploadLimits.ChunkSize, fileExists);
            }

            lock (_allocationLock)
            {
                var reservations = ReadReservations();
                var own = reservations.Where(x => x.UserId == userId).ToList();
                if (own.Count >= UploadLimits.MaxSessionsPerUser || reservations.Count >= UploadLimits.MaxSessionsTotal
                    || own.Sum(x => x.FileSize) + fileSize > UploadLimits.MaxPendingBytesPerUser
                    || reservations.Sum(x => x.FileSize) + fileSize > UploadLimits.MaxPendingBytesTotal)
                    return (false, "待上传文件已达到配额。请取消旧任务后重试。", null, 0, UploadLimits.ChunkSize, fileExists);
                if (!HasDiskSpace(_sessionsRoot, Math.Min(fileSize, UploadLimits.ChunkSize)))
                    return (false, "磁盘空间不足。请释放空间后重试。", null, 0, UploadLimits.ChunkSize, fileExists);

                var uploadId = Guid.NewGuid().ToString("N");
                var partPath = Path.Combine(userDir, uploadId + ".part");
                var meta = new UploadSessionMeta
                {
                    Version = 2,
                    UploadId = uploadId,
                    UserId = userId,
                    UserFolder = userFolder,
                    SubPath = subPath,
                    SafeRelativePath = safeRelativePath,
                    FileSize = fileSize,
                    ContentHash = contentHash,
                    AccessPath = accessPath,
                    PartPath = partPath,
                    SessionKey = sessionKey,
                    UpdatedAtUtc = DateTime.UtcNow,
                    ResolvedBaseDir = Path.GetFullPath(baseDir)
                };
                if (!_access.CanWrite(meta))
                    return (false, "上传权限已失效。请重新选择目录。", null, 0, UploadLimits.ChunkSize, false);
                SaveMeta(meta, userDir);
                if (!File.Exists(partPath))
                    File.Create(partPath).Dispose();

                return (true, null, uploadId, 0, UploadLimits.ChunkSize, fileExists);
            }
        }
        finally { gate.Release(); }
    }


    public async Task<(bool ok, string? error, long uploadedBytes)> WriteChunkAsync(
        string userId, string uploadId, long offset, Stream body, CancellationToken ct)
    {
        // 获取上传限速 (MB/s)
        var (upLimitMBps, _) = _service.GetBandwidthLimit(userId);

        var gate = GetGate(userId);
        await gate.WaitAsync(ct);
        try
        {
            var meta = LoadMeta(userId, uploadId);
            if (meta == null || !_access.CanWrite(meta))
            {
                if (meta != null) DeleteSession(meta, GetUserDir(userId));
                return (false, "上传权限已失效。请重新选择目录。", 0);
            }
            var currentLen = GetPartLength(meta.PartPath);
            if (offset < 0 || offset > meta.FileSize) return (false, "偏移量超出文件大小", currentLen);
            if (offset != currentLen) return (false, "上传位置已变化。请重新初始化上传。", currentLen);

            await using var fs = new FileStream(meta.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(offset, SeekOrigin.Begin);

            // 带限速和实时速度追踪的流拷贝
            var buffer = new byte[65536];
            long totalCopied = 0;
            long batchBytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    if (!_access.CanWrite(meta))
                    {
                        await fs.DisposeAsync();
                        DeleteSession(meta, GetUserDir(userId));
                        return (false, "上传权限已失效。请重新选择目录。", 0);
                    }
                    if (totalCopied + read > UploadLimits.ChunkSize || offset + totalCopied + read > meta.FileSize)
                        throw new InvalidDataException("分块超过大小限制。请按规定大小重新上传。");
                    if (!HasDiskSpace(meta.PartPath, read))
                        throw new IOException("磁盘空间不足。请释放空间后重试。");
                    await fs.WriteAsync(buffer, 0, read, ct);
                    totalCopied += read;
                    batchBytes += read;

                    // 每约 256KB 更新一次速度追踪
                    if (batchBytes >= 262144)
                    {
                        _service.TrackUserUploadBytes(userId, batchBytes);
                        batchBytes = 0;
                    }

                    if (upLimitMBps > 0)
                    {
                        var targetMs = totalCopied / (upLimitMBps * 1048576.0) * 1000.0;
                        var delay = targetMs - sw.ElapsedMilliseconds;
                        if (delay > 0)
                            await Task.Delay((int)Math.Min(delay, 1000), ct);
                    }
                }
                // 补上尾部不足 256KB 的字节
                if (batchBytes > 0)
                    _service.TrackUserUploadBytes(userId, batchBytes);

                var uploaded = fs.Length;
                await fs.FlushAsync(ct);
                meta.UpdatedAtUtc = DateTime.UtcNow;
                SaveMeta(meta, GetUserDir(userId));
                return (true, null, uploaded);
            }
            catch
            {
                // Only append at the acknowledged offset, so rollback preserves prior chunks.
                if (fs.CanWrite) { fs.SetLength(offset); fs.Flush(true); }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public (bool ok, string? error, string? relativePath) Complete(string userId, string uploadId, bool overwrite = false)
    {
        var gate = GetGate(userId);
        gate.Wait();
        try
        {
            var meta = LoadMeta(userId, uploadId);
            if (meta == null || !_access.CanWrite(meta))
            {
                if (meta != null) DeleteSession(meta, GetUserDir(userId));
                return (false, "上传权限已失效。请重新选择目录。", null);
            }
            var uploaded = GetPartLength(meta.PartPath);
            if (uploaded != meta.FileSize)
                return (false, $"文件未传完（{uploaded}/{meta.FileSize} 字节）", null);

            if (meta.ContentHash.Length == 64)
            {
                using var input = File.OpenRead(meta.PartPath);
                var actualHash = Convert.ToHexString(SHA256.HashData(input));
                if (!string.Equals(actualHash, meta.ContentHash, StringComparison.OrdinalIgnoreCase))
                    return (false, "文件校验失败。请取消后重新上传。", null);
            }

            var baseDir = meta.ResolvedBaseDir ?? (meta.UserFolder == "__public__"
                ? _service.GetPublicSharePath()
                : _service.ResolveBaseDirectory(meta.UserFolder));
            var targetDir = string.IsNullOrEmpty(meta.SubPath) ? baseDir : Path.Combine(baseDir, meta.SubPath);
            var destPath = Path.Combine(targetDir, meta.SafeRelativePath);
            if (!FileShareService.IsPathUnderDirectory(baseDir, destPath))
                return (false, "无效的目标路径", null);

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            if (File.Exists(destPath) && !overwrite)
                return (false, "同名文件已存在。请确认覆盖后重试。", null);
            if (!HasDiskSpace(destDir!, meta.FileSize))
                return (false, "目标磁盘空间不足。请释放空间后重试。", null);

            // Stage on the destination volume, then atomically publish without deleting the old file.
            var staged = Path.Combine(destDir!, ".upload-" + meta.UploadId + ".tmp");
            try
            {
                using (var source = File.OpenRead(meta.PartPath))
                using (var target = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    File.SetAttributes(staged, FileAttributes.Hidden);
                    source.CopyTo(target);
                    target.Flush(true);
                }
                if (!_access.CanWrite(meta))
                {
                    DeleteSession(meta, GetUserDir(userId));
                    return (false, "上传权限已失效。请重新选择目录。", null);
                }
                File.SetAttributes(staged, FileAttributes.Normal);
                if (overwrite && File.Exists(destPath)) File.Replace(staged, destPath, null);
                else File.Move(staged, destPath); // A newly-created conflicting file must not be overwritten.
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }

            _service.AddLog("UPLD", "客户端", $"接收文件: {meta.SafeRelativePath} ({FormatSize(meta.FileSize)})");

            DeleteSession(meta, GetUserDir(userId));
            return (true, null, meta.SafeRelativePath);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildSessionKey(string userId, string destination, long fileSize, string contentHash, string accessPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { userId, destination, fileSize, contentHash, accessPath })))).ToLowerInvariant();

    private string GetUserDir(string userId) => Path.Combine(_sessionsRoot,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))));

    private SemaphoreSlim GetGate(string userId) => _locks[(uint)StringComparer.Ordinal.GetHashCode(userId) % (uint)_locks.Length];

    private static long GetPartLength(string partPath) =>
        File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

    private UploadSessionMeta? FindExistingSession(string userDir, string sessionKey)
    {
        if (!Directory.Exists(userDir)) return null;
        foreach (var metaFile in Directory.GetFiles(userDir, "*.json"))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(metaFile), JsonOpts);
                if (meta != null && meta.SessionKey == sessionKey)
                {
                    var validated = LoadMeta(meta.UserId, meta.UploadId);
                    if (validated != null && File.Exists(validated.PartPath)) return validated;
                }
            }
            catch { }
        }
        return null;
    }

    private UploadSessionMeta? LoadMeta(string userId, string uploadId)
    {
        if (!Guid.TryParseExact(uploadId, "N", out _)) return null;
        var path = Path.Combine(GetUserDir(userId), uploadId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(path), JsonOpts);
            if (meta == null || meta.UploadId != uploadId || meta.UserId != userId || meta.Version != 2) return null;
            var expectedPart = Path.Combine(GetUserDir(userId), uploadId + ".part");
            if (!string.Equals(meta.PartPath, expectedPart, StringComparison.OrdinalIgnoreCase)) return null;
            if (meta.UpdatedAtUtc <= DateTime.UtcNow - UploadLimits.SessionLifetime)
            {
                DeleteSession(meta, GetUserDir(userId));
                return null;
            }
            return meta;
        }
        catch { return null; }
    }

    private static void SaveMeta(UploadSessionMeta meta, string userDir)
    {
        var path = Path.Combine(userDir, meta.UploadId + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(meta, JsonOpts));
        File.Move(temporary, path, true);
    }

    private static void DeleteSession(UploadSessionMeta meta, string userDir)
    {
        var metaPath = Path.Combine(userDir, meta.UploadId + ".json");
        if (File.Exists(metaPath)) File.Delete(metaPath);
        if (File.Exists(meta.PartPath)) File.Delete(meta.PartPath);
    }

    public void CancelSession(string userId, string uploadId)
    {
        var gate = GetGate(userId);
        gate.Wait();
        try
        {
            var meta = LoadMeta(userId, uploadId);
            if (meta != null) DeleteSession(meta, GetUserDir(userId));
        }
        finally { gate.Release(); }
    }

    private List<UploadSessionMeta> ReadReservations()
    {
        var result = new List<UploadSessionMeta>();
        foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(path), JsonOpts);
                if (meta != null && meta.FileSize > 0 && meta.FileSize <= UploadLimits.MaxFileSize) result.Add(meta);
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return result;
    }

    public void CleanupExpiredSessions()
    {
        try
        {
            var cutoff = DateTime.UtcNow - UploadLimits.SessionLifetime;
            foreach (var directory in Directory.EnumerateDirectories(_sessionsRoot))
            {
                // Read the owner only to select its stripe; validate paths before deleting anything.
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(path), JsonOpts);
                        if (meta == null) continue;
                        var gate = GetGate(meta.UserId);
                        if (!gate.Wait(0)) continue;
                        try
                        {
                            meta = JsonSerializer.Deserialize<UploadSessionMeta>(File.ReadAllText(path), JsonOpts);
                            if (meta == null || meta.UpdatedAtUtc > cutoff) continue;
                            File.Delete(path);
                            var part = Path.ChangeExtension(path, ".part");
                            if (File.Exists(part)) File.Delete(part);
                        }
                        finally { gate.Release(); }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var part in Directory.EnumerateFiles(directory, "*.part"))
                {
                    if (!File.Exists(Path.ChangeExtension(part, ".json")) && File.GetLastWriteTimeUtc(part) <= cutoff)
                        File.Delete(part);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool HasDiskSpace(string path, long required)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace >= required + UploadLimits.ReservedDiskBytes; }
        catch (IOException) { return false; }
    }

    public void Dispose() => _cleanup.Dispose();

    private static string SanitizeRelativePath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safe = parts
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != "..")
            .Select(p => string.Join("_", p.Split(Path.GetInvalidFileNameChars())));
        return string.Join(Path.DirectorySeparatorChar.ToString(), safe);
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
}
