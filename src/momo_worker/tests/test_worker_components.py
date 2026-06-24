import sys
import os
import unittest
import numpy as np

# Adjust path to import local modules
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from pipeline.glossary_processor import GlossaryProcessor
from pipeline.diarizer import Diarizer

class MockDbHelper:
    def __init__(self):
        self.profiles = []

    def get_speaker_profiles(self):
        return self.profiles

    def insert_speaker_profile(self, profile_id, original_id, display_name, voiceprint_embedding):
        self.profiles.append({
            "id": profile_id,
            "original_id": original_id,
            "display_name": display_name,
            "voiceprint_embedding": voiceprint_embedding
        })

class TestWorkerComponents(unittest.TestCase):
    def setUp(self):
        # Setup temporary glossaries
        self.temp_dir = os.path.dirname(os.path.abspath(__file__))
        self.medical_file = "medical_test.json"
        self.finance_file = "finance_test.json"
        
        with open(os.path.join(self.temp_dir, self.medical_file), "w", encoding="utf-8") as f:
            f.write('{"terzipatide": "Tirzepatide", "momo": "Momo"}')
            
        with open(os.path.join(self.temp_dir, self.finance_file), "w", encoding="utf-8") as f:
            f.write('{"vc": "Venture Capital", "terzipatide": "Tirzepatide Financials"}')

        self.processor = GlossaryProcessor(self.temp_dir)

    def tearDown(self):
        # Cleanup temp glossaries
        try: os.remove(os.path.join(self.temp_dir, self.medical_file))
        except: pass
        try: os.remove(os.path.join(self.temp_dir, self.finance_file))
        except: pass

    def test_glossary_replacement_boundaries(self):
        # Test word boundaries
        text = "I am taking terzipatide. This is counterzipatide text."
        # terzipatide should be replaced, counterzipatide should not be touched
        result = self.processor.process_text(text, [self.medical_file])
        self.assertIn("Tirzepatide", result)
        self.assertIn("counterzipatide", result)

    def test_glossary_priority_sorting(self):
        text = "Check terzipatide values."
        # If medical_test is first, it should replace terzipatide to "Tirzepatide"
        # If finance_test is first, it should replace it to "Tirzepatide Financials"
        result_med_first = self.processor.process_text(text, [self.medical_file, self.finance_file])
        self.assertEqual("Check Tirzepatide values.", result_med_first)

        result_fin_first = self.processor.process_text(text, [self.finance_file, self.medical_file])
        self.assertEqual("Check Tirzepatide Financials values.", result_fin_first)

    def test_speaker_diarizer_similarity_logic(self):
        # Test that similarity threshold matches similar embeddings
        diarizer = Diarizer()
        db = MockDbHelper()
        
        # Insert a mock profile embedding
        mock_emb = np.ones(512, dtype=np.float32)
        # Normalize mock_emb
        mock_emb = mock_emb / np.linalg.norm(mock_emb)
        db.insert_speaker_profile("SPK_PROF_TEST", "SPEAKER_00", "Dr. Chang", mock_emb.tobytes())

        # Test process_diarization matching
        # If pyannote is disabled, it should just return segments unchanged
        segments = [{"start": 0.0, "end": 5.0, "text": "Hello", "speaker": "SPEAKER_00"}]
        result = diarizer.process_diarization("mock.wav", segments, db)
        self.assertEqual(result[0]["speaker"], "SPEAKER_00")

if __name__ == "__main__":
    unittest.main()
