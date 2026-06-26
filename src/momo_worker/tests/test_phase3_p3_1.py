import sys
import os
import unittest
import numpy as np
from fastapi.testclient import TestClient

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from main import app, args
from pipeline.rag_orchestrator import RagOrchestrator
from pipeline.streaming_server import StreamingProcessor

class TestPhase3P3_1(unittest.TestCase):
    def setUp(self):
        # Configure test credentials
        args.token = "test_token"
        args.db = "test_momo.db" # Mock DB path for setup
        self.client = TestClient(app)
        self.headers = {"Authorization": "Bearer test_token"}

    def test_rag_orchestrator_local_mode(self):
        orchestrator = RagOrchestrator()
        # Ingest
        project_id = "test_proj_1"
        media_file_id = "test_media_1"
        segments = [
            {"start": 0.0, "end": 2.0, "text": "This is a segment about Tirzepatide medical trials.", "speaker": "Speaker_00"},
            {"start": 2.0, "end": 4.0, "text": "This is a segment about venture capital investments.", "speaker": "Speaker_01"}
        ]
        ingest_res = orchestrator.ingest_segments(project_id, media_file_id, segments)
        self.assertEqual(ingest_res["status"], "SUCCESS")
        self.assertEqual(ingest_res["count"], 2)

        # Query
        results = orchestrator.query_segments(project_id, "Tirzepatide medical", limit=1)
        self.assertEqual(len(results), 1)
        self.assertIn("Tirzepatide", results[0]["payload"]["text"])

        # Filter check (querying different project_id should yield no results)
        empty_results = orchestrator.query_segments("different_project", "Tirzepatide", limit=1)
        self.assertEqual(len(empty_results), 0)

    def test_rag_endpoints(self):
        # Ingest via API
        payload = {
            "project_id": "test_proj_api",
            "media_file_id": "test_media_api",
            "segments": [
                {"start": 1.0, "end": 3.0, "text": "Analyzing financial projections for next quarter.", "speaker": "Speaker_00"}
            ]
        }
        response = self.client.post("/rag/ingest", json=payload, headers=self.headers)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json()["status"], "SUCCESS")

        # Query via API
        query_payload = {
            "project_id": "test_proj_api",
            "query": "financial projections",
            "limit": 5
        }
        query_response = self.client.post("/rag/query", json=query_payload, headers=self.headers)
        self.assertEqual(query_response.status_code, 200)
        results = query_response.json()["results"]
        self.assertTrue(len(results) > 0)
        self.assertIn("financial", results[0]["payload"]["text"])

    def test_streaming_processor_and_websocket(self):
        processor = StreamingProcessor()
        # Test with 1 second of silence PCM
        pcm_1s = np.zeros(16000, dtype=np.int16).tobytes()
        res = processor.transcribe_pcm(pcm_1s)
        self.assertIsInstance(res, str)

        # Connect to WebSocket endpoint
        with self.client.websocket_connect("/ws/live-stream?token=test_token") as websocket:
            # Send 10s of PCM data (10 * 32000 bytes)
            pcm_10s = np.zeros(160000, dtype=np.int16).tobytes()
            websocket.send_bytes(pcm_10s)
            
            # Since it's silence, it should return a partial transcript JSON
            resp = websocket.receive_json()
            self.assertIn("text", resp)
            self.assertEqual(resp["status"], "partial")

if __name__ == "__main__":
    unittest.main()
