using System.IO;
using Microsoft.Data.Sqlite;

namespace VoiceAgend.App.Services;

/// <summary>
/// Lokaler Verlauf: speichert die letzten 50 Transkripte in SQLite unter %AppData%\VoiceAgend\history.db.
/// Wird vom Verlaufs-Sidebar im Transkript-Tab gelesen.
/// </summary>
public sealed class HistoryService
{
    public sealed record Entry(
        long Id,
        DateTime CreatedAt,
        string Title,
        string Text,
        string? Language,
        int DurationMs,
        int ProcessingMs,
        bool Pinned);

    public event Action? Changed;

    private const int MaxEntries = 50;
    private readonly string _dbPath;
    private readonly string _connectionString;

    public HistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceAgend");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "history.db");
        _connectionString = $"Data Source={_dbPath}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS transcripts (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at    INTEGER NOT NULL,
                    title         TEXT    NOT NULL,
                    text          TEXT    NOT NULL,
                    language      TEXT,
                    duration_ms   INTEGER NOT NULL DEFAULT 0,
                    processing_ms INTEGER NOT NULL DEFAULT 0,
                    pinned        INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_transcripts_created ON transcripts(created_at DESC);
            ";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Error("HistoryService.EnsureSchema", ex);
        }
    }

    public Entry Add(string text, string? language, int durationMs, int processingMs)
    {
        var title = BuildTitle(text);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long newId;
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO transcripts (created_at, title, text, language, duration_ms, processing_ms)
                VALUES ($t, $title, $text, $lang, $dur, $proc);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", ts);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$lang", (object?)language ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", durationMs);
            cmd.Parameters.AddWithValue("$proc", processingMs);
            newId = (long)(cmd.ExecuteScalar() ?? 0L);

            using var trim = conn.CreateCommand();
            // Behalte gepinnte + die letzten 50 nicht-gepinnten
            trim.CommandText = @"
                DELETE FROM transcripts
                WHERE pinned = 0 AND id NOT IN (
                    SELECT id FROM transcripts WHERE pinned = 0 ORDER BY created_at DESC LIMIT $n
                );";
            trim.Parameters.AddWithValue("$n", MaxEntries);
            trim.ExecuteNonQuery();
        }
        Changed?.Invoke();
        return new Entry(newId, DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime,
            title, text, language, durationMs, processingMs, false);
    }

    public IReadOnlyList<Entry> List(string? filter = null, int limit = 100)
    {
        var result = new List<Entry>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(filter)
                ? "SELECT id, created_at, title, text, language, duration_ms, processing_ms, pinned FROM transcripts ORDER BY pinned DESC, created_at DESC LIMIT $n"
                : "SELECT id, created_at, title, text, language, duration_ms, processing_ms, pinned FROM transcripts WHERE title LIKE $q OR text LIKE $q ORDER BY pinned DESC, created_at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            if (!string.IsNullOrWhiteSpace(filter))
                cmd.Parameters.AddWithValue("$q", "%" + filter + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new Entry(
                    Id: r.GetInt64(0),
                    CreatedAt: DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).LocalDateTime,
                    Title: r.GetString(2),
                    Text: r.GetString(3),
                    Language: r.IsDBNull(4) ? null : r.GetString(4),
                    DurationMs: r.GetInt32(5),
                    ProcessingMs: r.GetInt32(6),
                    Pinned: r.GetInt32(7) != 0));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("HistoryService.List", ex);
        }
        return result;
    }

    public void TogglePin(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE transcripts SET pinned = 1 - pinned WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke();
    }

    public void Delete(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transcripts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke();
    }

    private static string BuildTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(leer)";
        // Erste 60 Zeichen als Titel, an Wortgrenze abschneiden
        var trimmed = text.Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (trimmed.Length <= 60) return trimmed;
        var cut = trimmed[..60];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 30 ? cut[..lastSpace] : cut) + "…";
    }
}
