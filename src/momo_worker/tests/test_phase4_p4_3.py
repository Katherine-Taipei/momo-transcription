import sys
import os
import unittest
from fastapi.testclient import TestClient

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app, args, db_helper

class TestPhase4P4_3(unittest.TestCase):
    def setUp(self):
        import sqlite3
        # Configure shared in-memory DB for test isolation
        args.db = "file:test_momo_in_mem_p4_3?mode=memory&cache=shared"
        args.token = "test_token_p4_3"
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
        self.headers = {"Authorization": "Bearer test_token_p4_3"}
        
        # Insert a mock project and transcript to satisfy foreign key constraints
        with db_helper._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("INSERT OR REPLACE INTO projects (id, name) VALUES ('proj_1', 'Project One')")
            cursor.execute("""
                INSERT OR REPLACE INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at)
                VALUES ('trans_1', 'proj_1', 'media_1', 'Mock raw text contents.', '2026-06-29T12:00:00', '2026-06-29T12:00:00')
            """)
            conn.commit()

    def tearDown(self):
        if hasattr(self, "keep_alive_conn"):
            self.keep_alive_conn.close()

    def test_create_revision_success(self):
        payload = {
            "transcript_id": "trans_1",
            "created_by": "UserA",
            "description": "Manual backup description"
        }
        response = self.client.post("/api/v1/revisions", json=payload, headers=self.headers)
        self.assertEqual(response.status_code, 201)
        
        data = response.json()
        self.assertEqual(data["transcript_id"], "trans_1")
        self.assertEqual(data["created_by"], "UserA")
        self.assertEqual(data["description"], "Manual backup description")
        self.assertEqual(data["snapshot_text"], "Mock raw text contents.")
        self.assertEqual(data["version_number"], 1)
        self.assertIn("id", data)
        self.assertIn("created_at", data)
        
        # Verify Location header
        location = response.headers.get("Location")
        self.assertEqual(location, f"/api/v1/revisions/{data['id']}")
        
        # Verify DB content
        db_rev = db_helper.get_revision(data["id"])
        self.assertIsNotNone(db_rev)
        self.assertEqual(db_rev["snapshot_text"], "Mock raw text contents.")
        self.assertEqual(db_rev["version_number"], 1)

    def test_create_revision_version_increments(self):
        payload = {
            "transcript_id": "trans_1",
            "created_by": "UserA",
            "description": "Manual backup 1"
        }
        # First revision (version 1)
        r1 = self.client.post("/api/v1/revisions", json=payload, headers=self.headers)
        self.assertEqual(r1.status_code, 201)
        self.assertEqual(r1.json()["version_number"], 1)

        # Second revision (version 2)
        payload["description"] = "Manual backup 2"
        r2 = self.client.post("/api/v1/revisions", json=payload, headers=self.headers)
        self.assertEqual(r2.status_code, 201)
        self.assertEqual(r2.json()["version_number"], 2)

    def test_create_revision_transcript_not_found(self):
        payload = {
            "transcript_id": "trans_not_exist",
            "created_by": "UserA",
            "description": "Fail expected"
        }
        response = self.client.post("/api/v1/revisions", json=payload, headers=self.headers)
        self.assertEqual(response.status_code, 404)

    def test_get_transcript_revisions(self):
        # Insert revisions directly
        db_helper.insert_revision("r_1", "trans_1", 1, "UserA", "Version 1 text", "Manual v1")
        db_helper.insert_revision("r_2", "trans_1", 2, "UserB", "Version 2 text", "Manual v2")
        
        response = self.client.get("/api/v1/transcripts/trans_1/revisions", headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(len(data), 2)
        # Should be sorted version number DESC
        self.assertEqual(data[0]["id"], "r_2")
        self.assertEqual(data[0]["version_number"], 2)
        self.assertEqual(data[1]["id"], "r_1")
        self.assertEqual(data[1]["version_number"], 1)

    def test_get_revision_detail(self):
        db_helper.insert_revision("r_100", "trans_1", 1, "UserA", "Version 1 snapshot text", "Manual v1")
        
        response = self.client.get("/api/v1/revisions/r_100", headers=self.headers)
        self.assertEqual(response.status_code, 200)
        
        data = response.json()
        self.assertEqual(data["id"], "r_100")
        self.assertEqual(data["version_number"], 1)
        self.assertEqual(data["snapshot_text"], "Version 1 snapshot text")

    def test_get_revision_detail_not_found(self):
        response = self.client.get("/api/v1/revisions/r_999", headers=self.headers)
        self.assertEqual(response.status_code, 404)

    def test_get_transcript_revisions_not_found(self):
        response = self.client.get("/api/v1/transcripts/trans_999/revisions", headers=self.headers)
        self.assertEqual(response.status_code, 404)

if __name__ == "__main__":
    unittest.main()
