using System.Text;

namespace LanFileShare;

public static class LogSources
{
    public const string Server = "服务端";
    public const string Client = "客户端";
}

public static class DailyFileLog
{
    private static readonly object FileLock = new();
    private static readonly string LogDir = Path.Combine(AppPaths.DataDirectory, "log");

    public static void Append(LogEntry entry)
    {
        try
        {
            var dateKey = entry.Time.ToString("yyyyMMdd");
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"{dateKey}.log");
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}";
            lock (FileLock)
            {
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 文件日志失败不影响主流程
        }
    }
}
