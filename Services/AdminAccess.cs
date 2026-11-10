using System.Net;

namespace LanFileShare;

public static class AdminAccess
{
    public static bool IsLocalRequest(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip != null && IPAddress.IsLoopback(ip);
    }

    // 管理后台 API 路径判断（客户端 API 对局域网开放，非客户端 API 仅本地访问）
    public static bool IsAdminPath(PathString path)
    {
        var p = path.Value ?? "";
        if (string.IsNullOrEmpty(p) || p == "/") return true;
        if (p.StartsWith("/client", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.StartsWith("/api/files", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Equals("/api/heartbeat", StringComparison.OrdinalIgnoreCase)) return false;
        return p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
    }
}
