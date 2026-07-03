import sys
import os
import unittest
import time
from fastapi.testclient import TestClient

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app, args, db_helper

class TestPhase5P5_1(unittest.TestCase):
    def setUp(self):
        import sqlite3
        # Configure shared in-memory DB for test isolation
        args.db = "file:test_momo_in_mem_p5_1?mode=memory&cache=shared"
        args.token = "test_token_p5_1"
        db_helper.db_path = args.db
        
        # Keep connection open to prevent DB destruction
        self.keep_alive_conn = sqlite3.connect(args.db, uri=True)
        
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
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
            conn.commit()
            
        db_helper._initialize_db()
        self.client = TestClient(app)
        self.headers = {"Authorization": "Bearer test_token_p5_1"}

    def tearDown(self):
        self.keep_alive_conn.close()

    def test_tokenize_text(self):
        # CJK characters, English words, punctuation
        text = "Hello, 世界！這是一個 test."
        tokens = db_helper.tokenize_text(text)
        expected = ["Hello", ",", "世", "界", "！", "這", "是", "一", "個", "test", "."]
        self.assertEqual(tokens, expected)

    def test_rebuild_index_and_alignment(self):
        transcript_id = "t_1"
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, "p1", "m1", "我今天去商店。", "2026-07-03", "2026-07-03")
            )
            # Insert original sentence
            cursor.execute(
                "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, "我今天去商店。", 0.0, 7.0, "Speaker_01", 0.95)
            )
            conn.commit()

        # Update raw_text to add "便利" (Convenience) before "商店" (Shop)
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("UPDATE transcripts SET raw_text = '我今天去便利商店。' WHERE id = ?", (transcript_id,))
            conn.commit()

        # Trigger rebuild
        inserted_count, duration = db_helper.rebuild_transcript_index(transcript_id)
        self.assertTrue(inserted_count > 0)
        self.assertTrue(duration >= 0)

        # Retrieve new words
        new_words = db_helper.get_transcript_words(transcript_id)
        # Expected tokens: ['我', '今', '天', '去', '便', '利', '商', '店', '。']
        self.assertEqual(len(new_words), 9)

        # Verify CJK words aligned and inherited speaker/timing properly
        self.assertEqual(new_words[0]["word"], "我")
        self.assertEqual(new_words[0]["speaker_id"], "Speaker_01")
        self.assertAlmostEqual(new_words[0]["start_time"], 0.0)

        # The newly added "便" and "利" should have timing interpolated sequentially
        self.assertEqual(new_words[4]["word"], "便")
        self.assertEqual(new_words[5]["word"], "利")
        self.assertTrue(new_words[4]["start_time"] < new_words[5]["start_time"])

    def test_search_api_endpoint(self):
        transcript_id = "t_2"
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, "p1", "m1", "這是一個完整的搜尋測試。我們可以用關鍵字來搜尋段落。", "2026-07-03", "2026-07-03")
            )
            conn.commit()

        # Rebuild index first
        db_helper.rebuild_transcript_index(transcript_id)

        # Search for "搜尋"
        resp = self.client.get(f"/api/v1/transcripts/{transcript_id}/search?q=搜尋", headers=self.headers)
        self.assertEqual(resp.status_code, 200)
        data = resp.json()
        self.assertEqual(len(data), 2)  # "搜尋" matches in both "這是一個完整的搜尋測試。" and "我們可以用關鍵字來搜尋段落。"
        
        # Verify first match details
        self.assertEqual(data[0]["paragraph_id"], "para_0")
        self.assertEqual(data[0]["paragraph_index"], 0)
        self.assertTrue("搜尋" in data[0]["context"])

    def test_rebuild_index_stress_test_10k(self):
        transcript_id = "t_stress"
        # 10,000 characters text (repeating pattern)
        large_text = "這是一個高負載的效能壓力測試。" * 650 # ~10k characters
        
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, "p1", "m1", large_text, "2026-07-03", "2026-07-03")
            )
            # Seed initial matching word segment
            cursor.execute(
                "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, "這是一個高負載的效能壓力測試。", 0.0, 10.0, "Speaker_01", 0.99)
            )
            conn.commit()

        # Run index rebuild with stress timing
        start_time = time.time()
        inserted_count, duration_ms = db_helper.rebuild_transcript_index(transcript_id)
        elapsed = time.time() - start_time

        # Validate stress performance
        print(f"Stress test completed: {inserted_count} words rebuilt in {duration_ms:.2f}ms (actual elapsed: {elapsed:.2f}s)")
        self.assertTrue(inserted_count > 5000)
        self.assertTrue(elapsed < 5.0, f"Rebuild stress test took too long: {elapsed:.2f}s")
