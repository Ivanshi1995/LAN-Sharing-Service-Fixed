using System.Collections.Concurrent;

namespace LanFileShare;

public class ClientSession
{
    public string Token { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    // [已废弃] 向后兼容，优先用 Folders
    public string Folder { get; set; } = "";
    // 用户共享目录列表
    public List<string> Folders { get; set; } = new();
    public bool ReadOnly { get; set; } = false;
    public DateTime ExpiresAt { get; set; }
}

public class AuthService
{
    public const string SessionCookieName = "LanFileShare.Session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly UserService _users;

    public AuthService(UserService users)
    {
        _users = users;
    }

    public (bool success, string? error, ClientSession? session) Login(string username, string password)
    {
        var user = _users.FindByUsername(username);
        if (user == null) return (false, "用户名或密码错误", null);
        if (user.Pending) return (false, "账户正在等待管理员审批", null);
        if (!user.Enabled) return (false, "账户已禁用", null);
        if (!_users.VerifyPassword(user, password)) return (false, "用户名或密码错误", null);

        var session = new ClientSession
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Username = user.Username,
            Folder = user.Folders.Count > 0 ? user.Folders[0] : (user.Folder ?? ""),
            Folders = user.Folders.Count > 0 ? user.Folders : (string.IsNullOrEmpty(user.Folder) ? new List<string>() : new List<string> { user.Folder }),
            ReadOnly = user.ReadOnly,
            ExpiresAt = DateTime.UtcNow.Add(SessionLifetime)
        };
        _sessions[session.Token] = session;
        return (true, null, session);
    }

    public void OnLoginSuccess(ClientSession session, string ip) => TouchOnline(session, ip);

    public void SetSessionCookie(HttpContext ctx, ClientSession session)
    {
        ctx.Response.Cookies.Append(SessionCookieName, session.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = session.ExpiresAt,
            Path = "/"
        });
    }

    public void ClearSessionCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });
    }

    public ClientSession? GetSession(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(SessionCookieName, out var token) || string.IsNullOrEmpty(token))
            return null;
        return GetActiveSession(token);
    }

    internal ClientSession? GetActiveSession(string token)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        var user = _users.FindById(session.UserId);
        if (user == null || !user.Enabled)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        session.Folder = user.Folders.Count > 0 ? user.Folders[0] : (user.Folder ?? "");
        session.Folders = user.Folders.Count > 0 ? user.Folders : (string.IsNullOrEmpty(user.Folder) ? new List<string>() : new List<string> { user.Folder });
        session.Username = user.Username;
        session.ReadOnly = user.ReadOnly;
        return session;
    }

    public void Logout(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(SessionCookieName, out var token)
            && _sessions.TryGetValue(token, out var session))
        {
            SetOffline(session.UserId);
            _sessions.TryRemove(token, out _);
        }
        ClearSessionCookie(ctx);
    }

    private readonly ConcurrentDictionary<string, (string Username, string Ip, DateTime LastSeenUtc)> _onlineByUserId = new();
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(20);

    public void TouchOnline(ClientSession session, string ip)
    {
        _onlineByUserId[session.UserId] = (session.Username, ip, DateTime.UtcNow);
    }

    public void SetOffline(string userId) => _onlineByUserId.TryRemove(userId, out _);

    public List<OnlineUserDto> GetOnlineUsers()
    {
        var cutoff = DateTime.UtcNow - OnlineTimeout;
        return _onlineByUserId
            .Where(kv => kv.Value.LastSeenUtc >= cutoff)
            .Select(kv => new OnlineUserDto { Username = kv.Value.Username, Ip = kv.Value.Ip })
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CleanupOnlineStale()
    {
        var cutoff = DateTime.UtcNow - OnlineTimeout;
        foreach (var kv in _onlineByUserId)
        {
            if (kv.Value.LastSeenUtc < cutoff)
                _onlineByUserId.TryRemove(kv.Key, out _);
        }
    }
}

public class OnlineUserDto
{
    public string Username { get; set; } = "";
    public string Ip { get; set; } = "";
}
