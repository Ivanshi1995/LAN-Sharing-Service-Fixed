using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanFileShare.UI;

namespace LanFileShare;

public class VersionManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("release_notes")]
    public string ReleaseNotes { get; set; } = "";

    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class UpdateChangedFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}

public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public int ChangedCount { get; set; }
    public long TotalBytes { get; set; }
    public List<UpdateChangedFile> ChangedFiles { get; set; } = new();
    public string? Error { get; set; }
}

public class UpdateApplyResult
{
    public bool Success { get; set; }
    public bool NeedRestart { get; set; }
    public string Message { get; set; } = "";
    public int DownloadedCount { get; set; }
    public int AppliedNowCount { get; set; }
    public int PendingRestartCount { get; set; }
    public string? Error { get; set; }
}

public class UpdateService
{
    public const string UpdateBaseUrl = "https://lan.sharing.service.lahong.top";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _appBaseDir;
    private readonly FileShareService _logService;
    private readonly string _stagingDir;
    private readonly string _restartScriptPath;

    public UpdateService(FileShareService logService)
    {
        _logService = logService;
        _appBaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _stagingDir = Path.Combine(_appBaseDir, ".update", "pending");
        _restartScriptPath = Path.Combine(_appBaseDir, ".update", "apply.cmd");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = AppVersion.Version };
        if (AppPaths.IsSingleFile)
        {
            result.Error = "请从项目发布页下载新版程序，关闭后替换当前文件。";
            return result;
        }
        try
        {
            var remote = await FetchRemoteManifestAsync(ct);
            if (remote == null)
            {
                result.Error = "无法获取云端版本信息";
                return result;
            }

            result.LatestVersion = remote.Version;
            result.ReleaseNotes = remote.ReleaseNotes ?? "";
            var changed = await GetChangedFilesAsync(remote, ct);
            result.ChangedFiles = changed;
            result.ChangedCount = changed.Count;
            result.TotalBytes = changed.Sum(f => f.Size);
            result.HasUpdate = changed.Count > 0;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logService.AddLog("ERROR", "服务端", $"检测更新失败: {ex.Message}");
        }
        return result;
    }

    public async Task<UpdateApplyResult> ApplyUpdatesAsync(CancellationToken ct = default)
    {
        var result = new UpdateApplyResult();
        if (AppPaths.IsSingleFile)
        {
            result.Error = "请从项目发布页下载新版程序，关闭后替换当前文件。";
            return result;
        }
        try
        {
            var remote = await FetchRemoteManifestAsync(ct);
            if (remote == null)
            {
                result.Error = "无法获取云端版本信息";
                return result;
            }

            var changed = await GetChangedFilesAsync(remote, ct);
            if (changed.Count == 0)
            {
                result.Success = true;
                result.Message = "当前已是最新版本";
                return result;
            }

            _logService.AddLog("INFO", "服务端", $"更新 {changed.Count} 个文件...");
            Directory.CreateDirectory(_stagingDir);

            var restartFiles = new List<string>();
            var appliedNow = 0;

            foreach (var item in changed)
            {
                ct.ThrowIfCancellationRequested();
                var normalizedPath = NormalizeRelativePath(item.Path);
                var stagedPath = await DownloadFileAsync(normalizedPath, ct);
                result.DownloadedCount++;

                if (RequiresRestart(normalizedPath))
                {
                    restartFiles.Add(normalizedPath);
                    _logService.AddLog("INFO", "服务端", $"待重启: {normalizedPath}");
                }
                else
                {
                    ApplyFile(normalizedPath, stagedPath);
                    appliedNow++;
                    _logService.AddLog("INFO", "服务端", $"已更新: {normalizedPath}");
                }
            }

            result.AppliedNowCount = appliedNow;
            result.PendingRestartCount = restartFiles.Count;

            if (restartFiles.Count > 0)
            {
                CreateRestartScript(restartFiles);
                result.NeedRestart = true;
                result.Success = true;
                result.Message = $"已下载 {result.DownloadedCount} 个文件，其中 {restartFiles.Count} 个需重启后生效";
                _logService.AddLog("INFO", "服务端", result.Message);
            }
            else
            {
                CleanupStaging();
                result.Success = true;
                result.Message = $"更新完成，共更新 {appliedNow} 个文件";
                _logService.AddLog("INFO", "服务端", result.Message);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Message = "更新失败";
            _logService.AddLog("ERROR", "服务端", $"更新失败: {ex.Message}");
        }
        return result;
    }

    public bool LaunchRestartUpdate()
    {
        if (!File.Exists(_restartScriptPath))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = _restartScriptPath,
            WorkingDirectory = _appBaseDir,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return true;
    }

    public static void ScheduleProcessExit(CancellationTokenSource? serverCts, MainForm? mainForm)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            try { serverCts?.Cancel(); } catch { }

            try
            {
                if (mainForm != null && !mainForm.IsDisposed)
                    mainForm.BeginInvoke(mainForm.RequestExitForUpdate);
                else
                    Environment.Exit(0);
            }
            catch
            {
                Environment.Exit(0);
            }

            await Task.Delay(5000);
            Environment.Exit(0);
        });
    }

    private async Task<VersionManifest?> FetchRemoteManifestAsync(CancellationToken ct)
    {
        var url = $"{UpdateBaseUrl.TrimEnd('/')}/version.json?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<VersionManifest>(stream, JsonOpts, ct);
    }

    private async Task<List<UpdateChangedFile>> GetChangedFilesAsync(VersionManifest remote, CancellationToken ct)
    {
        var changed = new List<UpdateChangedFile>();
        foreach (var (relativePath, remoteHash) in remote.Files)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = NormalizeRelativePath(relativePath);
            var localPath = ToLocalPath(normalizedPath);
            if (!File.Exists(localPath) || !string.Equals(ComputeFileHash(localPath), remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                long size = 0;
                try
                {
                    using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, BuildFileUrl(normalizedPath)), ct);
                    if (head.IsSuccessStatusCode && head.Content.Headers.ContentLength.HasValue)
                        size = head.Content.Headers.ContentLength.Value;
                }
                catch
                {
                    if (File.Exists(localPath))
                        size = new FileInfo(localPath).Length;
                }

                changed.Add(new UpdateChangedFile { Path = normalizedPath, Size = size });
            }
        }
        return changed;
    }

    private async Task<string> DownloadFileAsync(string relativePath, CancellationToken ct)
    {
        var destPath = Path.Combine(_stagingDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        var url = BuildFileUrl(relativePath);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载失败 ({(int)response.StatusCode}): {relativePath} ({url})");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);
        await stream.CopyToAsync(file, ct);
        return destPath;
    }

    private void ApplyFile(string relativePath, string stagedPath)
    {
        var destPath = ToLocalPath(relativePath);
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);
        File.Copy(stagedPath, destPath, true);
    }

    private void CreateRestartScript(IEnumerable<string> relativePaths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_restartScriptPath)!);
        var exePath = Path.Combine(_appBaseDir, "LanFileShare.exe");
        var updateRoot = Path.Combine(_appBaseDir, ".update");
        var pid = Environment.ProcessId;
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal EnableDelayedExpansion");
        sb.AppendLine($"set TARGET_PID={pid}");
        sb.AppendLine("set /a WAIT=0");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %TARGET_PID%\" 2>NUL | find \"%TARGET_PID%\" >NUL");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  set /a WAIT+=1");
        sb.AppendLine("  if !WAIT! LSS 60 (timeout /t 1 /nobreak >nul & goto wait)");
        sb.AppendLine("  taskkill /PID %TARGET_PID% /F /T >nul 2>&1");
        sb.AppendLine("  timeout /t 2 /nobreak >nul");
        sb.AppendLine(")");

        foreach (var relativePath in relativePaths)
        {
            var src = Path.Combine(_stagingDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var dst = ToLocalPath(relativePath);
            var dstDir = Path.GetDirectoryName(dst);
            sb.AppendLine($"if not exist \"{dstDir}\" mkdir \"{dstDir}\"");
            sb.AppendLine($"copy /Y \"{src}\" \"{dst}\" >nul");
            sb.AppendLine("if errorlevel 1 (echo 更新失败: 无法覆盖 " + relativePath + " & pause & exit /b 1)");
        }

        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine($"if exist \"{updateRoot}\" rd /s /q \"{updateRoot}\"");
        sb.AppendLine("endlocal");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(_restartScriptPath, sb.ToString(), new UTF8Encoding(false));
    }

    private void CleanupStaging()
    {
        var updateRoot = Path.Combine(_appBaseDir, ".update");
        if (Directory.Exists(updateRoot))
            Directory.Delete(updateRoot, true);
    }

    private static bool RequiresRestart(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        const string marker = "net8.0-windows/";
        var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            normalized = normalized[(idx + marker.Length)..];
        return normalized;
    }

    private static string BuildFileUrl(string relativePath)
    {
        var encoded = string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));
        return $"{UpdateBaseUrl.TrimEnd('/')}/{encoded}";
    }

    private string ToLocalPath(string relativePath) =>
        Path.Combine(_appBaseDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ComputeFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
