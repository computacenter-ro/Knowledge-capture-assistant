using System.Text.Json;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services.Anonymization;
using Microsoft.Data.Sqlite;

namespace KnowledgeCapture.Core.Services;

public sealed record MessageToStore(ChatRole Role, AnonText Content, string ModelId, string AnonymizerVersion,
    IReadOnlyDictionary<string, int> EntityCounts);

public sealed record StoredConversation(string Id, string Title, string Topic, DateTime CreatedUtc, string CoverageKeys,
    string? RollingSummary, IReadOnlyList<StoredMessage> Messages);

/// <summary>
/// SQLite storage. Every text column is anonymized: the write API only accepts <see cref="AnonText"/>.
/// The placeholder mapping is never stored anywhere.
/// </summary>
public sealed class ConversationStore
{
    private readonly string _cs;
    public string DbPath { get; }

    public ConversationStore(string dbPath)
    {
        DbPath = dbPath;
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=DELETE;
            PRAGMA secure_delete=ON;
            CREATE TABLE IF NOT EXISTS conversations(
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL DEFAULT '',
              topic TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL,
              coverage TEXT NOT NULL DEFAULT '',
              rolling_summary TEXT,
              employee_hash TEXT NOT NULL,
              model_id TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS messages(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              conversation_id TEXT NOT NULL REFERENCES conversations(id),
              seq INTEGER NOT NULL,
              role TEXT NOT NULL,
              content TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              model_id TEXT NOT NULL,
              anonymizer_version TEXT NOT NULL,
              entity_counts TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IF NOT EXISTS ix_messages_conv ON messages(conversation_id, seq);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("O");
    private static DateTime ParseIso(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    // ------------------------------------------------------------------ settings

    public bool StoreEnabled
    {
        get => GetSetting("store_enabled") != "0";
        set => SetSetting("store_enabled", value ? "1" : "0");
    }

    private string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetSetting(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ writes (anonymized only)

    /// <summary>Stores one turn atomically (both messages or neither). Creates the conversation row on first write.</summary>
    public void AddMessages(string conversationId, string topic, string employeeHash, IReadOnlyList<MessageToStore> messages)
    {
        if (messages.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Iso(DateTime.UtcNow);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO conversations(id, topic, created_utc, updated_utc, employee_hash, model_id)
                VALUES($id, $topic, $now, $now, $emp, $model)
                ON CONFLICT(id) DO UPDATE SET updated_utc=$now, model_id=$model
                """;
            cmd.Parameters.AddWithValue("$id", conversationId);
            cmd.Parameters.AddWithValue("$topic", topic);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$emp", employeeHash);
            cmd.Parameters.AddWithValue("$model", messages[^1].ModelId);
            cmd.ExecuteNonQuery();
        }
        long seq;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(MAX(seq), -1) FROM messages WHERE conversation_id=$id";
            cmd.Parameters.AddWithValue("$id", conversationId);
            seq = (long)cmd.ExecuteScalar()!;
        }
        foreach (var m in messages)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages(conversation_id, seq, role, content, created_utc, model_id, anonymizer_version, entity_counts)
                VALUES($id, $seq, $role, $content, $now, $model, $anon, $counts)
                """;
            cmd.Parameters.AddWithValue("$id", conversationId);
            cmd.Parameters.AddWithValue("$seq", ++seq);
            cmd.Parameters.AddWithValue("$role", m.Role == ChatRole.User ? "user" : "assistant");
            cmd.Parameters.AddWithValue("$content", m.Content.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$model", m.ModelId);
            cmd.Parameters.AddWithValue("$anon", m.AnonymizerVersion);
            cmd.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(m.EntityCounts));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool Exists(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM conversations WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", conversationId);
        return cmd.ExecuteScalar() is not null;
    }

    public void UpdateCoverage(string conversationId, string coverageKeys) =>
        Update(conversationId, "coverage", coverageKeys);

    public void UpdateTitle(string conversationId, AnonText title) => Update(conversationId, "title", title.Value);

    public void UpdateRollingSummary(string conversationId, AnonText summary) =>
        Update(conversationId, "rolling_summary", summary.Value);

    private void Update(string conversationId, string column, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE conversations SET {column}=$v, updated_utc=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ reads

    public List<ConversationSummary> ListConversations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.title, c.topic, c.created_utc, c.updated_utc, c.coverage,
                   (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=c.id AND m.role='user'),
                   (SELECT COUNT(*) FROM messages m WHERE m.conversation_id=c.id),
                   (SELECT json_group_array(m.entity_counts) FROM messages m WHERE m.conversation_id=c.id)
            FROM conversations c ORDER BY c.updated_utc DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationSummary>();
        while (r.Read())
            list.Add(new ConversationSummary(r.GetString(0), r.GetString(1), r.GetString(2), ParseIso(r.GetString(3)),
                ParseIso(r.GetString(4)), r.GetString(5), r.GetInt32(6), r.GetInt32(7), SumCounts(r.GetString(8))));
        return list;
    }

    public StoredConversation? Load(string conversationId)
    {
        using var c = Open();
        string title, topic, coverage; string? summary; DateTime created;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT title, topic, coverage, rolling_summary, created_utc FROM conversations WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", conversationId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            (title, topic, coverage) = (r.GetString(0), r.GetString(1), r.GetString(2));
            summary = r.IsDBNull(3) ? null : r.GetString(3);
            created = ParseIso(r.GetString(4));
        }
        return new StoredConversation(conversationId, title, topic, created, coverage, summary, Messages(c, conversationId));
    }

    private static List<StoredMessage> Messages(SqliteConnection c, string conversationId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, seq, role, content, created_utc, model_id, anonymizer_version, entity_counts
            FROM messages WHERE conversation_id=$id ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read())
            list.Add(new StoredMessage(r.GetInt64(0), conversationId, r.GetInt32(1),
                r.GetString(2) == "user" ? ChatRole.User : ChatRole.Assistant, r.GetString(3), ParseIso(r.GetString(4)),
                r.GetString(5), r.GetString(6), ParseCounts(r.GetString(7))));
        return list;
    }

    private static Dictionary<string, int> ParseCounts(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static Dictionary<string, int> SumCounts(string jsonArrayOfObjects)
    {
        var total = new Dictionary<string, int>();
        try
        {
            foreach (var s in JsonSerializer.Deserialize<List<string>>(jsonArrayOfObjects) ?? [])
                foreach (var (k, v) in ParseCounts(s)) total[k] = total.GetValueOrDefault(k) + v;
        }
        catch (JsonException) { }
        return total;
    }
}
