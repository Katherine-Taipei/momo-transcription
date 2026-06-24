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
    }
}
