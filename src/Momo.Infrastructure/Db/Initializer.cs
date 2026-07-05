using Microsoft.EntityFrameworkCore;

namespace Momo.Infrastructure.Db;

public static class Initializer
{
    public static void Initialize(AppDbContext context)
    {
        context.Database.EnsureCreated();

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
            ";
            command.ExecuteNonQuery();
        }

        // Migrate/create speaker_profiles if not exists
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS speaker_profiles (
                    id TEXT PRIMARY KEY,
                    original_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    voiceprint_embedding BLOB NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_speaker_display_name ON speaker_profiles(display_name);
            ";
            command.ExecuteNonQuery();
        }

        // Migrate/create comments and tasks tables if not exist
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS comments (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    paragraph_id TEXT NOT NULL,
                    author TEXT NOT NULL,
                    text TEXT NOT NULL,
                    parent_id TEXT NULL,
                    status TEXT NOT NULL DEFAULT 'open',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE,
                    FOREIGN KEY(parent_id) REFERENCES comments(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_comments_paragraph_created ON comments(paragraph_id, created_at);

                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    paragraph_id TEXT NOT NULL,
                    assignee TEXT NOT NULL,
                    author TEXT NOT NULL,
                    text TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'open',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_tasks_paragraph_created ON tasks(paragraph_id, created_at);

                CREATE TABLE IF NOT EXISTS revisions (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    version_number INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    description TEXT NULL,
                    snapshot_text TEXT NOT NULL,
                    status TEXT CHECK(status IN ('pending','accepted','rejected')) DEFAULT 'pending',
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE,
                    UNIQUE(transcript_id, version_number)
                );
                CREATE INDEX IF NOT EXISTS idx_revisions_transcript_version ON revisions(transcript_id, version_number);

                CREATE TABLE IF NOT EXISTS embedding_cache (
                    text_hash TEXT PRIMARY KEY,
                    embedding_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_embedding_cache_created_at ON embedding_cache(created_at);

                CREATE TABLE IF NOT EXISTS telemetry_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    event_name TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_telemetry_logs_session_id ON telemetry_logs(session_id);
            ";
            command.ExecuteNonQuery();
        }

        // Migrate/alter job_queue to add new metadata columns if they don't exist
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA table_info(job_queue);";
            using var reader = checkCmd.ExecuteReader();
            var columns = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            if (!columns.Contains("selected_glossaries"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE job_queue ADD COLUMN selected_glossaries TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
            if (!columns.Contains("selected_role"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE job_queue ADD COLUMN selected_role TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
            if (!columns.Contains("selected_template"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE job_queue ADD COLUMN selected_template TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }

        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA table_info(revisions);";
            using var reader = checkCmd.ExecuteReader();
            var columns = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            reader.Close();

            if (!columns.Contains("status"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE revisions ADD COLUMN status TEXT CHECK(status IN ('pending','accepted','rejected')) DEFAULT 'pending';";
                alterCmd.ExecuteNonQuery();
            }
        }
    }
}
