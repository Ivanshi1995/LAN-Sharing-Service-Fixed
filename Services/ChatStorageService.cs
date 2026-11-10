using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace LanFileShare;

// SQLite热数据 + GZip归档的聊天存储
public class ChatStorageService : IDisposable
{
    private readonly string _dbPath;
    private readonly string _archiveDir;
    private readonly string _connectionString;
    private readonly object _dbLock = new();
    private readonly System.Threading.Timer _archiveTimer;
    private bool _disposed;

    // 内存中的热数据缓存（key = "userA|userB"，按字母排序）
    private readonly ConcurrentDictionary<string, List<ChatMessageRecord>> _hotCache = new();

    public ChatStorageService(string baseDir)
    {
        _archiveDir = Path.Combine(baseDir, "ChatArchive");
        Directory.CreateDirectory(_archiveDir);

        _dbPath = Path.Combine(_archiveDir, "chat_hot.db");
        _connectionString = $"Data Source={_dbPath}";

        InitDatabase();
        MigrateOldJsonlFiles();

        // 每天凌晨 2 点自动归档 7 天前的消息
        var now = DateTime.Now;
        var nextRun = now.Date.AddDays(1).AddHours(2);
        if (now > nextRun) nextRun = nextRun.AddDays(1);
        var delay = nextRun - now;
        _archiveTimer = new System.Threading.Timer(_ => RunArchive(), null, delay, TimeSpan.FromDays(1));
    }

    private void InitDatabase()
    {
        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS chat_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_key TEXT NOT NULL,
                    msg_type TEXT NOT NULL DEFAULT 'text',
                    content TEXT NOT NULL DEFAULT '',
                    sender TEXT NOT NULL,
                    recipient TEXT NOT NULL,
                    file_name TEXT,
                    file_url TEXT,
                    file_size INTEGER,
                    created_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_chat_conv_time ON chat_messages(conversation_key, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_chat_sender ON chat_messages(sender);
                CREATE INDEX IF NOT EXISTS idx_chat_created ON chat_messages(created_at);
            ";
            cmd.ExecuteNonQuery();
        }
    }

    // 兼容旧版.jsonl数据迁移
    private void MigrateOldJsonlFiles()
    {
        try
        {
            var jsonlFiles = Directory.GetFiles(_archiveDir, "chat_*.jsonl");
            foreach (var file in jsonlFiles)
            {
                var lines = File.ReadAllLines(file);
                var messages = new List<ChatMessageRecord>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var record = JsonSerializer.Deserialize<ChatMessageRecord>(line);
                        if (record != null) messages.Add(record);
                    }
                    catch { }
                }

                if (messages.Count > 0)
                {
                    foreach (var msg in messages)
                        InsertMessageInternal(msg);
                }

                // 迁移后删除旧文件
                try { File.Delete(file); } catch { }
            }

            // 也处理 .jsonl.gz 归档文件
            var gzFiles = Directory.GetFiles(_archiveDir, "chat_*.jsonl.gz");
            foreach (var gzFile in gzFiles)
            {
                try
                {
                    using var fs = new FileStream(gzFile, FileMode.Open);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var reader = new StreamReader(gz);
                    var content = reader.ReadToEnd();
                    var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        try
                        {
                            var record = JsonSerializer.Deserialize<ChatMessageRecord>(line);
                            if (record != null) InsertMessageInternal(record);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public static string GetConversationKey(string userA, string userB)
    {
        return string.Compare(userA, userB, StringComparison.Ordinal) < 0
            ? $"{userA}|{userB}"
            : $"{userB}|{userA}";
    }

    public void SaveMessage(string sender, string recipient, string type, string content,
        string? fileName = null, string? fileUrl = null, long? fileSize = null, long? time = null)
    {
        var record = new ChatMessageRecord
        {
            ConversationKey = GetConversationKey(sender, recipient),
            MsgType = type,
            Content = content,
            Sender = sender,
            Recipient = recipient,
            FileName = fileName,
            FileUrl = fileUrl,
            FileSize = fileSize,
            CreatedAt = time ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        InsertMessageInternal(record);

        // 更新热缓存
        var key = record.ConversationKey;
        _hotCache.AddOrUpdate(key,
            _ => new List<ChatMessageRecord> { record },
            (_, list) => { lock (list) { list.Add(record); } return list; });
    }

    private void InsertMessageInternal(ChatMessageRecord record)
    {
        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO chat_messages (conversation_key, msg_type, content, sender, recipient, file_name, file_url, file_size, created_at)
                VALUES (@key, @type, @content, @sender, @recipient, @fname, @furl, @fsize, @time)";
            cmd.Parameters.AddWithValue("@key", record.ConversationKey);
            cmd.Parameters.AddWithValue("@type", record.MsgType);
            cmd.Parameters.AddWithValue("@content", record.Content ?? "");
            cmd.Parameters.AddWithValue("@sender", record.Sender);
            cmd.Parameters.AddWithValue("@recipient", record.Recipient);
            cmd.Parameters.AddWithValue("@fname", (object?)record.FileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@furl", (object?)record.FileUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fsize", (object?)record.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@time", record.CreatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    public List<ChatMessageRecord> QueryMessages(string userA, string userB, int limit = 100, long beforeTime = long.MaxValue)
    {
        var key = GetConversationKey(userA, userB);
        var results = new List<ChatMessageRecord>();

        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT msg_type, content, sender, recipient, file_name, file_url, file_size, created_at
                FROM chat_messages
                WHERE conversation_key = @key AND created_at < @before
                ORDER BY created_at DESC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@before", beforeTime);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ChatMessageRecord
                {
                    ConversationKey = key,
                    MsgType = reader.GetString(0),
                    Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Sender = reader.GetString(2),
                    Recipient = reader.GetString(3),
                    FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FileUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    FileSize = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    CreatedAt = reader.GetInt64(7)
                });
            }
        }

        return results;
    }

    // 全局搜索
    public List<ChatMessageRecord> SearchMessages(string? keyword = null,
        string? sender = null, string? recipient = null,
        long? startTime = null, long? endTime = null,
        int limit = 200)
    {
        var results = new List<ChatMessageRecord>();
        var conditions = new List<string>();
        var parameters = new List<(string name, object value)>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            conditions.Add("(content LIKE @kw OR file_name LIKE @kw)");
            parameters.Add(("@kw", $"%{keyword}%"));
        }
        if (!string.IsNullOrWhiteSpace(sender))
        {
            conditions.Add("sender = @sender");
            parameters.Add(("@sender", sender));
        }
        if (!string.IsNullOrWhiteSpace(recipient))
        {
            conditions.Add("recipient = @recipient");
            parameters.Add(("@recipient", recipient));
        }
        if (startTime.HasValue)
        {
            conditions.Add("created_at >= @start");
            parameters.Add(("@start", startTime.Value));
        }
        if (endTime.HasValue)
        {
            conditions.Add("created_at <= @end");
            parameters.Add(("@end", endTime.Value));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $@"
                SELECT msg_type, content, sender, recipient, file_name, file_url, file_size, created_at, conversation_key
                FROM chat_messages
                {where}
                ORDER BY created_at DESC
                LIMIT @limit";
            foreach (var p in parameters)
                cmd.Parameters.AddWithValue(p.name, p.value);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ChatMessageRecord
                {
                    MsgType = reader.GetString(0),
                    Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Sender = reader.GetString(2),
                    Recipient = reader.GetString(3),
                    FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FileUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    FileSize = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    CreatedAt = reader.GetInt64(7),
                    ConversationKey = reader.GetString(8)
                });
            }
        }

        return results;
    }

    public List<string> GetConversationPartners(string username)
    {
        var partners = new HashSet<string>();
        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT sender, recipient FROM chat_messages
                WHERE sender = @u OR recipient = @u
                ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var s = reader.GetString(0);
                var r = reader.GetString(1);
                if (s != username) partners.Add(s);
                if (r != username) partners.Add(r);
            }
        }
        return partners.ToList();
    }

    // 每天凌晨2点自动跑，把7天前的消息归档
    public void RunArchive()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
            ArchiveBefore(cutoff);
        }
        catch { }
    }

    private void ArchiveBefore(long cutoffTime)
    {
        List<ChatMessageRecord> toArchive;

        lock (_dbLock)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
            db.Open();

            // 查询待归档消息
            using var selCmd = db.CreateCommand();
            selCmd.CommandText = @"
                SELECT msg_type, content, sender, recipient, file_name, file_url, file_size, created_at, conversation_key
                FROM chat_messages
                WHERE created_at < @cutoff
                ORDER BY created_at ASC";
            selCmd.Parameters.AddWithValue("@cutoff", cutoffTime);

            toArchive = new List<ChatMessageRecord>();
            using var reader = selCmd.ExecuteReader();
            while (reader.Read())
            {
                toArchive.Add(new ChatMessageRecord
                {
                    MsgType = reader.GetString(0),
                    Content = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Sender = reader.GetString(2),
                    Recipient = reader.GetString(3),
                    FileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FileUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    FileSize = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    CreatedAt = reader.GetInt64(7),
                    ConversationKey = reader.GetString(8)
                });
            }

            if (toArchive.Count == 0) return;

            // 写入 .jsonl.gz 归档文件
            var archiveDate = DateTimeOffset.FromUnixTimeMilliseconds(cutoffTime).ToString("yyyyMMdd");
            var archivePath = Path.Combine(_archiveDir, $"chat_{archiveDate}.jsonl.gz");

            using var fs = new FileStream(archivePath, FileMode.Append);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var writer = new StreamWriter(gz);
            foreach (var msg in toArchive)
            {
                writer.WriteLine(JsonSerializer.Serialize(msg));
            }
            writer.Flush();

            // 删除已归档消息
            using var delCmd = db.CreateCommand();
            delCmd.CommandText = "DELETE FROM chat_messages WHERE created_at < @cutoff";
            delCmd.Parameters.AddWithValue("@cutoff", cutoffTime);
            delCmd.ExecuteNonQuery();
        }
    }

    public List<ChatMessageRecord> LoadFromArchive(string userA, string userB, DateTime date)
    {
        var results = new List<ChatMessageRecord>();
        var archiveFile = Path.Combine(_archiveDir, $"chat_{date:yyyyMMdd}.jsonl.gz");

        if (!File.Exists(archiveFile)) return results;

        var key = GetConversationKey(userA, userB);

        using var fs = new FileStream(archiveFile, FileMode.Open);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var msg = JsonSerializer.Deserialize<ChatMessageRecord>(line);
                if (msg != null && msg.ConversationKey == key)
                    results.Add(msg);
            }
            catch { }
        }

        return results;
    }

    public List<DateTime> ListArchiveDates()
    {
        var dates = new List<DateTime>();
        try
        {
            foreach (var file in Directory.GetFiles(_archiveDir, "chat_*.jsonl.gz"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // chat_20260701.jsonl
                var datePart = name.Replace("chat_", "");
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var date))
                    dates.Add(date);
            }
        }
        catch { }
        dates.Sort();
        dates.Reverse();
        return dates;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archiveTimer?.Dispose();
    }
}

public class ChatMessageRecord
{
    public string ConversationKey { get; set; } = "";
    public string MsgType { get; set; } = "text";
    public string Content { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string? FileName { get; set; }
    public string? FileUrl { get; set; }
    public long? FileSize { get; set; }
    public long CreatedAt { get; set; }
}
