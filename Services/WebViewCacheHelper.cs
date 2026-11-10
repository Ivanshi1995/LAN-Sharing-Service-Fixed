using System.Text.Json;

namespace LanFileShare;

public static class WebViewCacheHelper
{
    public static string AppBaseDir => AppPaths.DataDirectory;

    private static string UploadSessionsRoot => Path.Combine(AppBaseDir, ".upload-sessions");

    private static readonly JsonSerializerOptions SessionJsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<string> GetCacheDirectories()
    {
        var primary = Path.Combine(AppBaseDir, "webview_cache");
        if (Directory.Exists(primary))
            yield return primary;

        if (!Directory.Exists(AppBaseDir))
            yield break;

        foreach (var dir in Directory.GetDirectories(AppBaseDir, "webview_cache_*"))
            yield return dir;
    }

    public static long GetDirectorySizeBytes(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch { /* skip locked files */ }
            }
        }
        catch { /* skip inaccessible dirs */ }

        return size;
    }

    public static long GetTotalCacheSizeBytes() =>
        GetCacheDirectories().Sum(GetDirectorySizeBytes) + GetDirectorySizeBytes(UploadSessionsRoot);

    public static long GbToBytes(double gb) =>
        (long)(gb * 1024L * 1024L * 1024L);

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        var order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* may be locked */ }
    }

    public static void DeleteOrphanCacheDirectories()
    {
        if (!Directory.Exists(AppBaseDir))
            return;

        foreach (var dir in Directory.GetDirectories(AppBaseDir, "webview_cache_*"))
            TryDeleteDirectory(dir);
    }

    public static void DeleteInactiveCacheDirectories(string? activeCacheDir)
    {
        var active = string.IsNullOrEmpty(activeCacheDir)
            ? null
            : Path.GetFullPath(activeCacheDir);

        foreach (var dir in GetCacheDirectories())
        {
            if (active != null &&
                string.Equals(Path.GetFullPath(dir), active, StringComparison.OrdinalIgnoreCase))
                continue;

            TryDeleteDirectory(dir);
        }
    }

    // 清理目录内所有内容，保留根目录
    public static long CleanDirectoryContents(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return 0;

        long cleaned = 0;

        // 先删除文件
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                var len = fi.Exists ? fi.Length : 0;
                fi.Delete();
                cleaned += len;
            }
            catch { /* 跳过被锁定的文件 */ }
        }

        // 再删除空子目录（从深层到浅层）
        try
        {
            foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                                          .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }

        return cleaned;
    }

    // 清理过期上传会话（.part + .json）
    public static long CleanupAbandonedUploadSessions(TimeSpan maxAge)
    {
        var root = UploadSessionsRoot;
        if (!Directory.Exists(root))
            return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        long cleanedBytes = 0;

        try
        {
            foreach (var userDir in Directory.GetDirectories(root))
            {
                foreach (var metaFile in Directory.GetFiles(userDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(metaFile);
                        using var doc = JsonDocument.Parse(json);
                        var rootEl = doc.RootElement;

                        DateTime updated;
                        if (rootEl.TryGetProperty("UpdatedAtUtc", out var utcEl)
                            && utcEl.TryGetDateTime(out updated)
                            && updated < cutoff)
                        {
                            // 获取对应的 .part 文件
                            string? partPath = null;
                            if (rootEl.TryGetProperty("PartPath", out var ppEl))
                                partPath = ppEl.GetString();

                            // 删除 .json
                            var fiJson = new FileInfo(metaFile);
                            cleanedBytes += fiJson.Exists ? fiJson.Length : 0;
                            fiJson.Delete();

                            // 删除 .part
                            if (!string.IsNullOrEmpty(partPath) && File.Exists(partPath))
                            {
                                var fiPart = new FileInfo(partPath);
                                cleanedBytes += fiPart.Exists ? fiPart.Length : 0;
                                fiPart.Delete();
                            }
                        }
                    }
                    catch { /* skip locked/corrupt files */ }
                }

                // 清理空用户目录
                try
                {
                    if (Directory.GetFileSystemEntries(userDir).Length == 0)
                        Directory.Delete(userDir);
                }
                catch { }
            }
        }
        catch { }

        return cleanedBytes;
    }
}

public class WebViewCacheCleanResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public long SizeBefore { get; set; }
    public long SizeAfter { get; set; }
    public string SizeBeforeDisplay { get; set; } = "";
    public string SizeAfterDisplay { get; set; } = "";
}
