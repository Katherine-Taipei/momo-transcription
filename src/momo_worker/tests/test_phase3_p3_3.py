import sys
import os
import unittest
from unittest.mock import MagicMock

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from pipeline.rag_orchestrator import RagOrchestrator, BM25

class TestPhase3P3_3(unittest.TestCase):
    def test_BM25_scoring(self):
        corpus = [
            {"text": "Apple is a tech company making smartphones"},
            {"text": "Clinical trials of Tirzepatide show high efficacy"},
            {"text": "Venture capital firms invest in biotechnology startups"}
        ]
        bm25 = BM25(corpus)
        
        # Query matching document 2
        scores = bm25.get_scores("Tirzepatide trials")
        self.assertTrue(scores[1] > scores[0])
        self.assertTrue(scores[1] > scores[2])
        
        # Query matching document 3
        scores_vc = bm25.get_scores("Venture capital")
        self.assertTrue(scores_vc[2] > scores_vc[0])
        self.assertTrue(scores_vc[2] > scores_vc[1])

    def test_RAG_MergeRank(self):
        orchestrator = RagOrchestrator()
        project_id = "proj_hybrid"
        media_id = "media_hybrid"
        
        segments = [
            {"start": 0.0, "end": 2.0, "text": "Analyzing Apple financial growth in smartphones.", "speaker": "Speaker_00"},
            {"start": 2.0, "end": 4.0, "text": "Biotechnology startups are gaining VC attention.", "speaker": "Speaker_01"},
            {"start": 4.0, "end": 6.0, "text": "Clinical trials evaluate medical efficiency.", "speaker": "Speaker_00"}
        ]
        # Ingest to in-memory Qdrant
        orchestrator.ingest_segments(project_id, media_id, segments)
        
        # Setup mock db helper returning some segments
        mock_db = MagicMock()
        mock_db.get_project_segments.return_value = [
            {"text": "Analyzing Apple financial growth in smartphones.", "start": 0.0, "end": 2.0, "speaker": "Speaker_00", "media_file_id": media_id},
            {"text": "Biotechnology startups are gaining VC attention.", "start": 2.0, "end": 4.0, "speaker": "Speaker_01", "media_file_id": media_id},
            {"text": "Clinical trials evaluate medical efficiency.", "start": 4.0, "end": 6.0, "speaker": "Speaker_00", "media_file_id": media_id}
        ]
        
        # Run hybrid search
        results = orchestrator.hybrid_search(mock_db, project_id, "smartphones", limit=2)
        
        self.assertTrue(len(results) > 0)
        self.assertTrue(len(results) <= 2)
        # Apple document should have high relevance
        top_text = results[0]["payload"]["text"]
        self.assertIn("Apple", top_text)

    def test_PromptBuilder_ContextSize(self):
        orchestrator = RagOrchestrator()
        
        # Setup long lists of segments and external documents
        segments = [
            {"score": 0.9, "payload": {"text": "Very long segment text that occupies a lot of characters in the final prompt " * 10, "speaker": "Speaker_A", "start": 10.0}},
            {"score": 0.8, "payload": {"text": "Second long segment that we might drop if context limit is reached " * 10, "speaker": "Speaker_B", "start": 20.0}},
            {"score": 0.7, "payload": {"text": "Third long segment that will definitely get truncated or omitted " * 10, "speaker": "Speaker_C", "start": 30.0}}
        ]
        
        external = [
            {"title": "External Study PubMed", "url": "https://pubmed.com/1", "pubdate": "2024"},
            {"title": "SEC Form 10-K AAPL", "url": "https://sec.gov/1", "filing_date": "2023"}
        ]
        
        # Limit prompt to 600 characters max
        max_chars = 600
        prompt = orchestrator.build_prompt(segments, external, "Smartphones technology", max_chars=max_chars)
        
        # Assertions
        self.assertTrue(len(prompt) <= max_chars, f"Prompt length {len(prompt)} exceeded max_chars {max_chars}")
        # Top segment is included (or part of it)
        self.assertIn("Speaker_A", prompt)
        # Omitted segment B or C should not be present in full if truncated
        self.assertTrue(len(prompt) > 0)

if __name__ == "__main__":
    unittest.main()
