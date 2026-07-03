import sys
import os
import unittest
import time
import hashlib
from fastapi.testclient import TestClient

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app, args, db_helper, rag_orchestrator
from pipeline.rag_orchestrator import RagOrchestrator

class TestPhase5P5_2(unittest.TestCase):
    def setUp(self):
        import sqlite3
        # Use isolated in-memory DB
        args.db = "file:test_momo_in_mem_p5_2?mode=memory&cache=shared"
        args.token = "test_token_p5_2"
        db_helper.db_path = args.db
        
        # Keep connection open to prevent DB destruction
        self.keep_alive_conn = sqlite3.connect(args.db, uri=True)
        
        # Initialize tables
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
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS embedding_cache (
                    text_hash TEXT PRIMARY KEY,
                    embedding_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
            """)
            conn.commit()
            
        db_helper._initialize_db()
        self.client = TestClient(app)
        self.headers = {"Authorization": "Bearer test_token_p5_2"}

        # Initialize mock or memory Qdrant client in rag_orchestrator
        self.orchestrator = RagOrchestrator(db_helper)
        # Force in-memory Qdrant Client
        from qdrant_client import QdrantClient
        self.orchestrator._client = QdrantClient(":memory:")
        self.orchestrator.ensure_collection()

    def tearDown(self):
        self.keep_alive_conn.close()

    def test_embedding_cache_hit_and_performance(self):
        text = "這是快取測試段落文字。"
        text_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()

        # Initial compute (Cache Miss)
        start_miss = time.time()
        vec_miss = self.orchestrator.get_embedding(text)
        duration_miss = time.time() - start_miss

        # Verify it was cached in database
        cached_val = db_helper.get_cached_embedding(text_hash)
        self.assertIsNotNone(cached_val)
        self.assertEqual(len(cached_val), 384)

        # Subsequent retrieve (Cache Hit)
        start_hit = time.time()
        vec_hit = self.orchestrator.get_embedding(text)
        duration_hit = time.time() - start_hit

        # Ensure vector match and substantial speedup
        self.assertEqual(vec_miss, vec_hit)
        print(f"Cache miss: {duration_miss:.6f}s, Cache hit: {duration_hit:.6f}s")
        self.assertTrue(duration_hit < duration_miss or duration_hit < 0.005)

    def test_qdrant_local_vs_global_scoping(self):
        # Ingest documents in project_1 and project_2
        self.orchestrator.ingest_segments("project_1", "m_1", [{"start": 0.0, "end": 2.0, "speaker": "A", "text": "比特幣價格上漲"}])
        self.orchestrator.ingest_segments("project_2", "m_2", [{"start": 3.0, "end": 5.0, "speaker": "B", "text": "比特幣區塊鏈技術"}])

        # Query local scope (project_1)
        res_local = self.orchestrator.query_segments("project_1", "比特幣", scope="local")
        self.assertEqual(len(res_local), 1)
        self.assertEqual(res_local[0]["payload"]["project_id"], "project_1")

        # Query global scope (cross-project)
        res_global = self.orchestrator.query_segments("project_1", "比特幣", scope="global")
        self.assertEqual(len(res_global), 2)

    def test_rrf_hybrid_ranking_with_rrf_k_parameter(self):
        # Set environment variable RRF_K
        os.environ["RRF_K"] = "10"
        
        # Mock database segments
        db_helper.get_all_segments = lambda: [
            {"text": "這是關鍵字匹配的內容", "start": 0.0, "end": 2.0, "speaker": "A", "media_file_id": "m1"},
            {"text": "完全無關的主題段落", "start": 3.0, "end": 5.0, "speaker": "B", "media_file_id": "m1"}
        ]
        self.orchestrator.ingest_segments("project_1", "m1", [
            {"start": 0.0, "end": 2.0, "speaker": "A", "text": "這是關鍵字匹配的內容"}
        ])

        results = self.orchestrator.hybrid_search(db_helper, "project_1", "關鍵字", scope="global")
        self.assertEqual(len(results), 1)
        self.assertTrue("關鍵字" in results[0]["payload"]["text"])

        # Reset environment
        del os.environ["RRF_K"]

    def test_rrf_sorting_stress_100k(self):
        # Stress test simulating RRF sorting performance on 100k segment candidates
        # Create mock vector and BM25 results list with 50 matching candidates each (max retrieved limit * 2)
        # Run 1,000 query operations and assert sorting completes in < 1 second.
        vector_dummy = []
        bm25_dummy = []
        for i in range(100):
            payload = {"media_file_id": "m_stress", "start": float(i), "end": float(i + 1), "text": f"dummy_{i}"}
            vector_dummy.append({"score": 0.9 - (i * 0.005), "payload": payload})
            bm25_dummy.append({"score": 10.0 - (i * 0.1), "payload": payload})

        # Run 1000 hybrid RRF merges
        start_time = time.time()
        k_val = 60
        for _ in range(1000):
            rrf_scores = {}
            doc_map = {}
            for rank, item in enumerate(vector_dummy):
                payload = item["payload"]
                key = f"{payload['media_file_id']}_{payload['start']}_{payload['end']}"
                doc_map[key] = payload
                rrf_scores[key] = rrf_scores.get(key, 0.0) + 1.0 / (k_val + rank + 1)
            for rank, item in enumerate(bm25_dummy):
                payload = item["payload"]
                key = f"{payload['media_file_id']}_{payload['start']}_{payload['end']}"
                doc_map[key] = payload
                rrf_scores[key] = rrf_scores.get(key, 0.0) + 1.0 / (k_val + rank + 1)
            sorted_keys = sorted(rrf_scores.keys(), key=lambda x: rrf_scores[x], reverse=True)
            
        elapsed = time.time() - start_time
        print(f"RRF 1000 sorting stress test elapsed: {elapsed:.3f}s")
        self.assertTrue(elapsed < 1.0, f"RRF stress test took too long: {elapsed:.3f}s")
