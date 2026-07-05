import sys
import os
import unittest
import subprocess
import tempfile
import sqlite3

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

class TestPhase5P5_3(unittest.TestCase):
    def setUp(self):
        # Create temp sqlite database file
        self.temp_db_fd, self.temp_db_path = tempfile.mkstemp(suffix=".db")
        os.close(self.temp_db_fd)
        
        # Initialize tables
        self.conn = sqlite3.connect(self.temp_db_path)
        cursor = self.conn.cursor()
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS transcripts (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                media_file_id TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
        """)
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS transcript_words (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                transcript_id TEXT NOT NULL,
                word TEXT NOT NULL,
                start_time REAL NOT NULL,
                end_time REAL NOT NULL,
                speaker_id TEXT NOT NULL,
                confidence REAL NOT NULL,
                FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
            );
        """)
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS transcript_index (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                transcript_id TEXT NOT NULL,
                word TEXT NOT NULL,
                start_time REAL NOT NULL,
                end_time REAL NOT NULL,
                speaker_id TEXT NOT NULL,
                FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
            );
        """)
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS comments (
                id TEXT PRIMARY KEY,
                transcript_id TEXT NOT NULL,
                paragraph_id TEXT NOT NULL,
                author TEXT NOT NULL,
                text TEXT NOT NULL,
                parent_id TEXT,
                status TEXT NOT NULL DEFAULT 'open',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
            );
        """)
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS revisions (
                id TEXT PRIMARY KEY,
                transcript_id TEXT NOT NULL,
                version_number INTEGER NOT NULL,
                snapshot_text TEXT NOT NULL,
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                status TEXT NOT NULL,
                FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
            );
        """)
        
        # Insert sample data for index rebuild testing
        cursor.execute(
            "INSERT INTO transcripts VALUES (?, ?, ?, ?, ?, ?)",
            ("t1", "p1", "m1", "這是一段測試文字。", "2026-07-04", "2026-07-04")
        )
        cursor.execute(
            "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
            ("t1", "這", 0.0, 0.5, "spk1", 0.99)
        )
        cursor.execute(
            "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
            ("t1", "是", 0.5, 1.0, "spk1", 0.99)
        )
        self.conn.commit()

    def tearDown(self):
        self.conn.close()
        if os.path.exists(self.temp_db_path):
            os.remove(self.temp_db_path)

    def test_rebuild_index_via_cli_subprocess(self):
        # Locate momoctl.py
        script_dir = os.path.dirname(os.path.abspath(__file__))
        momoctl_path = os.path.abspath(os.path.join(script_dir, "..", "momoctl.py"))
        
        cmd = [
            sys.executable,
            momoctl_path,
            "rebuild-index",
            "--db", self.temp_db_path,
            "--id", "t1"
        ]
        
        result = subprocess.run(cmd, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, f"momoctl failed: {result.stderr}")
        self.assertIn("Successfully rebuilt index", result.stdout)
        
        # Verify transcript_words content in SQLite
        cursor = self.conn.cursor()
        cursor.execute("SELECT count(*) FROM transcript_words WHERE transcript_id = ?", ("t1",))
        count = cursor.fetchone()[0]
        self.assertGreater(count, 0)

    def test_cli_version_flag(self):
        script_dir = os.path.dirname(os.path.abspath(__file__))
        momoctl_path = os.path.abspath(os.path.join(script_dir, "..", "momoctl.py"))
        
        cmd = [sys.executable, momoctl_path, "--version"]
        result = subprocess.run(cmd, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0)
        self.assertIn("momoctl 5.0.0-alpha2", result.stdout)

    def test_cli_empty_args_prints_help(self):
        script_dir = os.path.dirname(os.path.abspath(__file__))
        momoctl_path = os.path.abspath(os.path.join(script_dir, "..", "momoctl.py"))
        
        cmd = [sys.executable, momoctl_path]
        result = subprocess.run(cmd, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0)
        self.assertIn("momoctl - Momo Transcription Platform Command Line Tool", result.stdout)

if __name__ == "__main__":
    unittest.main()
