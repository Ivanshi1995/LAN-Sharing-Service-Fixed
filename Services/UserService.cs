using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanFileShare;

public class SharedFolderEntry
{
    public string Id { get; set; } = "";
    public string SourceUserId { get; set; } = "";
    public string SourceUsername { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool WritePermission { get; set; } = false;
    public bool IsFile { get; set; } = false;
    public DateTime SharedAt { get; set; }
}

public class OutgoingShareInfo
{
    public SharedFolderEntry Entry { get; set; } = new();
    public string TargetUserId { get; set; } = "";
    public string TargetUsername { get; set; } = "";
}

public class UserAccount
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    // [已废弃] 向后兼容旧数据
    public string Folder { get; set; } = "";
    // 用户可访问的目录列表
    public List<string> Folders { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
    public bool IsAdmin { get; set; } = false;
    public bool Pending { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public List<SharedFolderEntry> SharedFolders { get; set; } = new();
}

public class UserDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    // [已废弃] 向后兼容
    public string Folder { get; set; } = "";
    // 用户共享目录列表
    public List<string> Folders { get; set; } = new();
    public bool Enabled { get; set; }
    public bool ReadOnly { get; set; }
    public bool IsAdmin { get; set; }
    public bool Pending { get; set; }
    public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd HH:mm");
    public DateTime CreatedAt { get; set; }
}

public class ImportUserEntry
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
    public bool IsAdmin { get; set; } = false;
}

public class ImportUsersResult
{
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class UserService
{
    private readonly string _usersPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "users.json");
    private readonly object _lock = new();
    private readonly FileShareService _fileShare;
    private List<UserAccount> _users = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserService(FileShareService fileShare)
    {
        _fileShare = fileShare;
        Load();
    }

    public IReadOnlyList<UserDto> ListUsers()
    {
        lock (_lock)
        {
            return _users.Select(ToDto).OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public UserAccount? FindById(string id)
    {
        lock (_lock)
        {
            return _users.FirstOrDefault(u => u.Id == id);
        }
    }

    public UserAccount? FindByUsername(string username)
    {
        lock (_lock)
        {
            return _users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }
    }

    public (bool success, string? error, UserDto? user) CreateUser(string username, string password, List<string> folders, bool enabled, bool readOnly = false, bool isAdmin = false)
    {
        username = username.Trim();
        var normalizedFolders = folders
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!ValidateUsername(username, out var nameErr)) return (false, nameErr, null);
        if (string.IsNullOrEmpty(password)) return (false, "请设置密码", null);
        if (password.Length < 4) return (false, "密码至少 4 位", null);
        if (normalizedFolders.Count == 0) return (false, "请选择至少一个共享文件夹", null);
        foreach (var f in normalizedFolders)
        {
            if (!Directory.Exists(f)) return (false, $"共享文件夹不存在: {f}", null);
        }

        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return (false, "用户名已存在", null);

            var user = new UserAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Username = username,
                PasswordHash = PasswordHelper.Hash(password),
                Folders = normalizedFolders,
                Enabled = enabled,
                ReadOnly = readOnly,
                IsAdmin = isAdmin,
                CreatedAt = DateTime.Now
            };
            _users.Add(user);
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"新建用户 {username}" + (isAdmin ? "（管理员）" : ""));
            return (true, null, ToDto(user));
        }
    }

    public (bool success, string? error) UpdateUser(string id, string? password, List<string> folders, bool enabled, bool readOnly, bool? isAdmin = null)
    {
        var normalizedFolders = folders
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedFolders.Count == 0) return (false, "请选择至少一个共享文件夹");
        foreach (var f in normalizedFolders)
        {
            if (!Directory.Exists(f)) return (false, $"共享文件夹不存在: {f}");
        }
        if (!string.IsNullOrEmpty(password) && password.Length < 4) return (false, "密码至少 4 位");

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user == null) return (false, "用户不存在");

            user.Folders = normalizedFolders;
            user.Enabled = enabled;
            user.ReadOnly = readOnly;
            if (isAdmin.HasValue)
                user.IsAdmin = isAdmin.Value;
            if (!string.IsNullOrEmpty(password))
                user.PasswordHash = PasswordHelper.Hash(password);
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"更新用户 {user.Username}");
            return (true, null);
        }
    }

    public (bool success, string? error) DeleteUser(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user == null) return (false, "用户不存在");
            _users.Remove(user);
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"删除用户 {user.Username}");
            return (true, null);
        }
    }

    public ImportUsersResult ImportUsers(IReadOnlyList<ImportUserEntry> entries, string? defaultFolder = null)
    {
        var result = new ImportUsersResult();
        var defaultFolders = string.IsNullOrWhiteSpace(defaultFolder)
            ? new List<string>()
            : new List<string> { defaultFolder.Trim() };

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var lineNo = i + 1;
            var folders = string.IsNullOrWhiteSpace(entry.Folder) ? defaultFolders : new List<string> { entry.Folder.Trim() };
            var (ok, err, _) = CreateUser(entry.Username, entry.Password, folders, entry.Enabled, entry.ReadOnly, entry.IsAdmin);
            if (ok)
            {
                result.SuccessCount++;
            }
            else
            {
                result.FailedCount++;
                var name = string.IsNullOrWhiteSpace(entry.Username) ? "(空)" : entry.Username.Trim();
                result.Errors.Add($"第 {lineNo} 行 {name}: {err}");
            }
        }

        if (result.SuccessCount > 0)
            _fileShare.AddLog("INFO", "服务端", $"批量导入用户：成功 {result.SuccessCount}，失败 {result.FailedCount}");

        return result;
    }

    public (bool success, string? error) ChangePassword(string id, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword)) return (false, "请设置新密码");
        if (newPassword.Length < 4) return (false, "新密码至少 4 位");

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user == null) return (false, "用户不存在");
            if (!VerifyPassword(user, oldPassword)) return (false, "原密码错误");

            user.PasswordHash = PasswordHelper.Hash(newPassword);
            SaveLocked();
            _fileShare.AddLog("INFO", "客户端", $"用户 {user.Username} 修改密码");
            return (true, null);
        }
    }

    public List<SharedFolderEntry> GetSharedFolders(string userId)
    {
        var user = FindById(userId);
        if (user == null) return new List<SharedFolderEntry>();
        lock (_lock)
        {
            return user.SharedFolders.ToList();
        }
    }

    public List<OutgoingShareInfo> GetOutgoingShares(string sourceUserId)
    {
        lock (_lock)
        {
            var result = new List<OutgoingShareInfo>();
            foreach (var u in _users)
            {
                foreach (var sf in u.SharedFolders)
                {
                    if (sf.SourceUserId == sourceUserId)
                    {
                        result.Add(new OutgoingShareInfo
                        {
                            Entry = sf,
                            TargetUserId = u.Id,
                            TargetUsername = u.Username
                        });
                    }
                }
            }
            return result;
        }
    }

    public (bool success, string? error, SharedFolderEntry? entry) AddSharedFolder(
        string targetUserId, string sourceUserId, string sourceUsername,
        string sourcePath, string displayName, bool writePermission, bool isFile = false)
    {
        lock (_lock)
        {
            var targetUser = _users.FirstOrDefault(u => u.Id == targetUserId);
            if (targetUser == null)
                return (false, "目标用户不存在", null);

            if (targetUser.SharedFolders.Any(sf =>
                    string.Equals(sf.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)))
                return (false, "该" + (isFile ? "文件" : "文件夹") + "已共享给此用户", null);

            var entry = new SharedFolderEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceUserId = sourceUserId,
                SourceUsername = sourceUsername,
                SourcePath = sourcePath,
                DisplayName = displayName,
                WritePermission = writePermission,
                IsFile = isFile,
                SharedAt = DateTime.Now
            };

            targetUser.SharedFolders.Add(entry);
            SaveLocked();
            return (true, null, entry);
        }
    }

    public (bool success, string? error) RemoveSharedFolder(string targetUserId, string shareId)
    {
        lock (_lock)
        {
            var targetUser = _users.FirstOrDefault(u => u.Id == targetUserId);
            if (targetUser == null)
                return (false, "目标用户不存在");

            var removed = targetUser.SharedFolders.RemoveAll(sf => sf.Id == shareId);
            if (removed == 0)
                return (false, "共享记录不存在");

            SaveLocked();
            return (true, null);
        }
    }

    public bool VerifyPassword(UserAccount user, string password)
        => PasswordHelper.Verify(password, user.PasswordHash);

    // 自助注册，创建后待审批
    public (bool success, string? error) Register(string username, string password, string confirmPassword)
    {
        username = username.Trim();
        if (!ValidateUsername(username, out var nameErr)) return (false, nameErr);
        if (string.IsNullOrEmpty(password)) return (false, "请设置密码");
        if (password.Length < 4) return (false, "密码至少 4 位");
        if (password != confirmPassword) return (false, "两次密码不一致");

        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return (false, "用户名已存在");

            var user = new UserAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Username = username,
                PasswordHash = PasswordHelper.Hash(password),
                Folders = new List<string>(),  // 审批通过后由管理员设置
                Enabled = false,
                ReadOnly = false,
                IsAdmin = false,
                Pending = true,
                CreatedAt = DateTime.Now
            };
            _users.Add(user);
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"新用户注册申请：{username}（待审批）");
            return (true, null);
        }
    }

    // 审批通过，设置共享目录并启用
    public (bool success, string? error, UserDto? user) ApproveUser(string id, List<string> folders)
    {
        var normalizedFolders = folders
            .Select(f => f.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedFolders.Count == 0) return (false, "请选择至少一个共享文件夹", null);
        foreach (var f in normalizedFolders)
        {
            if (!Directory.Exists(f)) return (false, $"共享文件夹不存在: {f}", null);
        }

        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user == null) return (false, "用户不存在", null);
            if (!user.Pending) return (false, "该用户非待审批状态", null);

            user.Pending = false;
            user.Enabled = true;
            user.Folders = normalizedFolders;
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"审批通过注册用户：{user.Username}");
            return (true, null, ToDto(user));
        }
    }

    // 拒绝注册，删掉用户
    public (bool success, string? error) RejectUser(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user == null) return (false, "用户不存在");
            if (!user.Pending) return (false, "该用户非待审批状态");

            var username = user.Username;
            _users.Remove(user);
            SaveLocked();
            _fileShare.AddLog("INFO", "服务端", $"拒绝注册申请：{username}");
            return (true, null);
        }
    }

    // 待审批用户数
    public int GetPendingCount()
    {
        lock (_lock)
        {
            return _users.Count(u => u.Pending);
        }
    }

    private static bool ValidateUsername(string username, out string? error)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            error = "请输入用户名";
            return false;
        }
        if (!Regex.IsMatch(username, @"^[a-zA-Z0-9_\u4e00-\u9fa5]{2,32}$"))
        {
            error = "用户名 2-32 位，支持字母、数字、下划线或中文";
            return false;
        }
        error = null;
        return true;
    }

    private static UserDto ToDto(UserAccount u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Folder = u.Folders.Count > 0 ? u.Folders[0] : (u.Folder ?? ""), // 向后兼容
        Folders = u.Folders.Count > 0 ? u.Folders : (string.IsNullOrEmpty(u.Folder) ? new List<string>() : new List<string> { u.Folder }),
        Enabled = u.Enabled,
        ReadOnly = u.ReadOnly,
        IsAdmin = u.IsAdmin,
        Pending = u.Pending,
        CreatedAt = u.CreatedAt
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_usersPath)) return;
            var json = File.ReadAllText(_usersPath);
            _users = JsonSerializer.Deserialize<List<UserAccount>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<UserAccount>();
            // 向后兼容：将旧的单文件夹迁移到新列表
            bool migrated = false;
            foreach (var user in _users)
            {
                if (user.Folders.Count == 0 && !string.IsNullOrWhiteSpace(user.Folder))
                {
                    user.Folders = new List<string> { user.Folder };
                    migrated = true;
                }
            }
            if (migrated) SaveLocked();
        }
        catch
        {
            _users = new List<UserAccount>();
        }
    }

    private void SaveLocked()
    {
        File.WriteAllText(_usersPath, JsonSerializer.Serialize(_users, JsonOpts));
    }
}

public static class PasswordHelper
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.', 2);
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var hash = Convert.FromBase64String(parts[1]);
            var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(hash, computed);
        }
        catch
        {
            return false;
        }
    }
}
