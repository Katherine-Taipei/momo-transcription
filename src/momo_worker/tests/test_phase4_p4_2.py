import sys
import os
import unittest
from fastapi.testclient import TestClient

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app, args, db_helper

class TestPhase4P4_2(unittest.TestCase):
    def setUp(self):
        import sqlite3
        # Configure shared in-memory DB for test isolation
        args.db = "file:test_momo_in_mem?mode=memory&cache=shared"
        args.token = "test_token_p4_2"
        db_helper.db_path = args.db
        
        # Keep an open connection to prevent the shared in-memory DB from being destroyed
        self.keep_alive_conn = sqlite3.connect(args.db, uri=True)
        
        # Create dependent tables (projects and transcripts) which are normally managed by EF Core
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL
                );
            """)
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS transcripts (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    media_file_id TEXT NOT NULL,
                    raw_text TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(project_id) REFERENCES projects(id)
                );
            """)
            conn.commit()
            
        db_helper._initialize_db()
        
        self.client = TestClient(app)
        self.headers = {"Authorization": "Bearer test_token_p4_2"}
        
        # Insert a mock project and transcript to satisfy foreign key constraints
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("INSERT OR REPLACE INTO projects (id, name) VALUES ('proj_1', 'Project One')")
            cursor.execute("""
                INSERT OR REPLACE INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at)
                VALUES ('trans_1', 'proj_1', 'media_1', 'Mock raw text', '2026-06-29T12:00:00', '2026-06-29T12:00:00')
            """)
            conn.commit()

    def tearDown(self):
        if hasattr(self, "keep_alive_conn"):
            self.keep_alive_conn.close()

    def test_create_comment_success(self):
        payload = {
            "transcript_id": "trans_1",
            "paragraph_id": "para_1",
            "author": "Alice",
            "text": "This is a comment.",
            "parent_id": None
        }
        response = self.client.post("/api/v1/comments", json=payload, headers=self.headers)
        self.assertEqual(response.status_code, 201)
        
        data = response.json()
        self.assertEqual(data["transcript_id"], "trans_1")
        self.assertEqual(data["paragraph_id"], "para_1")
        self.assertEqual(data["author"], "Alice")
        self.assertEqual(data["text"], "This is a comment.")
        self.assertIsNone(data["parent_id"])
        self.assertEqual(data["status"], "open")
        self.assertIn("id", data)
        self.assertIn("created_at", data)
        self.assertIn("updated_at", data)
        
        # Verify Location header
        location = response.headers.get("Location")
        self.assertEqual(location, f"/api/v1/comments/{data['id']}")
        
        # Verify DB content
        db_comment = db_helper.get_comment(data["id"])
        self.assertIsNotNone(db_comment)
        self.assertEqual(db_comment["text"], "This is a comment.")

    def test_create_comment_unauthorized(self):
        payload = {
            "transcript_id": "trans_1",
            "paragraph_id": "para_1",
            "author": "Alice",
            "text": "Unauthorized."
        }
        response = self.client.post("/api/v1/comments", json=payload)
        self.assertEqual(response.status_code, 401)

    def test_patch_comment_success(self):
        # Insert a comment
        res = db_helper.insert_comment(
            comment_id="c_100",
            transcript_id="trans_1",
            paragraph_id="para_1",
            author="Alice",
            text="Initial comment text"
        )
        
        patch_payload = {
            "text": "Updated comment text",
            "status": "resolved"
        }
        response = self.client.patch("/api/v1/comments/c_100", json=patch_payload, headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(data["text"], "Updated comment text")
        self.assertEqual(data["status"], "resolved")
        
        # Verify DB
        db_comment = db_helper.get_comment("c_100")
        self.assertEqual(db_comment["text"], "Updated comment text")
        self.assertEqual(db_comment["status"], "resolved")

    def test_patch_comment_not_found(self):
        patch_payload = {"text": "Updated"}
        response = self.client.patch("/api/v1/comments/c_999", json=patch_payload, headers=self.headers)
        self.assertEqual(response.status_code, 404)

    def test_get_transcript_comments(self):
        # Insert multiple comments
        db_helper.insert_comment("c_1", "trans_1", "para_1", "Alice", "Comment 1")
        db_helper.insert_comment("c_2", "trans_1", "para_2", "Bob", "Comment 2", parent_id="c_1")
        
        response = self.client.get("/api/v1/transcripts/trans_1/comments", headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(len(data), 2)
        self.assertEqual(data[0]["id"], "c_1")
        self.assertEqual(data[1]["parent_id"], "c_1")

    def test_create_task_success(self):
        payload = {
            "transcript_id": "trans_1",
            "paragraph_id": "para_2",
            "assignee": "Bob",
            "author": "Alice",
            "text": "Review paragraph 2."
        }
        response = self.client.post("/api/v1/tasks", json=payload, headers=self.headers)
        self.assertEqual(response.status_code, 201)
        
        data = response.json()
        self.assertEqual(data["transcript_id"], "trans_1")
        self.assertEqual(data["paragraph_id"], "para_2")
        self.assertEqual(data["assignee"], "Bob")
        self.assertEqual(data["author"], "Alice")
        self.assertEqual(data["text"], "Review paragraph 2.")
        self.assertEqual(data["status"], "open")
        self.assertIn("id", data)
        
        # Verify Location
        location = response.headers.get("Location")
        self.assertEqual(location, f"/api/v1/tasks/{data['id']}")
        
        # Verify DB
        db_task = db_helper.get_task(data["id"])
        self.assertIsNotNone(db_task)
        self.assertEqual(db_task["assignee"], "Bob")

    def test_patch_task_status(self):
        db_helper.insert_task("t_100", "trans_1", "para_2", "Bob", "Alice", "Task desc")
        
        patch_payload = {"status": "resolved"}
        response = self.client.patch("/api/v1/tasks/t_100", json=patch_payload, headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(data["status"], "resolved")
        
        # Verify DB
        db_task = db_helper.get_task("t_100")
        self.assertEqual(db_task["status"], "resolved")

    def test_get_transcript_tasks(self):
        db_helper.insert_task("t_1", "trans_1", "para_1", "Bob", "Alice", "Task 1")
        db_helper.insert_task("t_2", "trans_1", "para_2", "Charlie", "Alice", "Task 2")
        
        response = self.client.get("/api/v1/transcripts/trans_1/tasks", headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(len(data), 2)
        self.assertEqual(data[0]["id"], "t_1")
        self.assertEqual(data[1]["id"], "t_2")

if __name__ == "__main__":
    unittest.main()
