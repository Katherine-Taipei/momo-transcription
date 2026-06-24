import os
import sys
import uuid
import numpy as np

class Diarizer:
    def __init__(self):
        self.enabled = False
        self.pipeline = None
        self.embedding_inference = None
        self.Segment = None
        try:
            from pyannote.audio import Pipeline, Model, Inference
            from pyannote.core import Segment
            self.Segment = Segment
            token = os.environ.get("HF_TOKEN")
            self.pipeline = Pipeline.from_pretrained("pyannote/speaker-diarization-3.1", use_auth_token=token)
            
            self.embedding_model = Model.from_pretrained("pyannote/embedding", use_auth_token=token)
            self.embedding_inference = Inference(self.embedding_model, window="whole")
            self.enabled = True
        except Exception as e:
            sys.stderr.write(f"[Diarization Warning] Failed to initialize Pyannote Diarization/Embedding: {e}. Falling back to disabled diarizer.\n")
            self.enabled = False

    def process_diarization(self, audio_path: str, segments: list, db_helper) -> list:
        """
        Applies speaker diarization and voiceprint matching.
        Uses SQLite db_helper to reconcile profiles across multiple audio files.
        """
        for seg in segments:
            if "speaker" not in seg:
                seg["speaker"] = "Speaker_00"

        if not self.enabled or not self.pipeline or not self.embedding_inference:
            return segments

        try:
            diarization_result = self.pipeline(audio_path)
            
            speaker_timeline = []
            for turn, _, speaker in diarization_result.itertracks(yield_label=True):
                speaker_timeline.append({
                    "start": turn.start,
                    "end": turn.end,
                    "speaker": speaker
                })
            
            if not speaker_timeline:
                return segments

            # 1. Group turns by speaker cluster label
            speaker_turns = {}
            for turn in speaker_timeline:
                spk = turn["speaker"]
                if spk not in speaker_turns:
                    speaker_turns[spk] = []
                speaker_turns[spk].append(turn)

            # 2. Query all existing speaker profiles from SQLite DB
            existing_profiles = db_helper.get_speaker_profiles()
            
            # Helper to parse blob embedding back to float numpy array
            def load_emb(blob):
                return np.frombuffer(blob, dtype=np.float32)

            # 3. Calculate weighted average embedding for each cluster and find match
            local_to_global_map = {}
            for local_spk, turns in speaker_turns.items():
                sum_emb = np.zeros(512, dtype=np.float32)
                sum_duration = 0.0

                for turn in turns:
                    duration = turn["end"] - turn["start"]
                    if duration < 0.5:
                        continue  # Skip extremely short segments to avoid noise in embedding
                    
                    try:
                        # Extract embedding crop
                        crop_seg = self.Segment(turn["start"], turn["end"])
                        emb = self.embedding_inference(audio_path, crop=crop_seg)
                        
                        # Weighted by duration
                        sum_emb += emb * duration
                        sum_duration += duration
                    except Exception:
                        # Fail gracefully for individual crop failures
                        pass

                if sum_duration == 0.0:
                    # Fallback to GUID or default if no segments were long enough
                    local_to_global_map[local_spk] = local_spk
                    continue

                avg_emb = sum_emb / sum_duration
                # L2 normalize avg_emb
                norm = np.linalg.norm(avg_emb)
                if norm > 0:
                    avg_emb = avg_emb / norm

                # 4. Compare with existing profiles
                best_profile_id = None
                max_similarity = -1.0

                for prof in existing_profiles:
                    try:
                        stored_emb = load_emb(prof["voiceprint_embedding"])
                        stored_norm = np.linalg.norm(stored_emb)
                        if stored_norm > 0:
                            stored_emb = stored_emb / stored_norm
                        
                        similarity = np.dot(avg_emb, stored_emb)
                        if similarity > max_similarity:
                            max_similarity = similarity
                            best_profile_id = prof["id"]
                    except Exception:
                        pass

                # Cosine Similarity threshold check
                if max_similarity >= 0.80 and best_profile_id is not None:
                    local_to_global_map[local_spk] = best_profile_id
                else:
                    # Create new global speaker profile
                    new_profile_id = f"SPK_PROF_{uuid.uuid4().hex[:8].upper()}"
                    display_name = local_spk  # Defaults to Pyannote speaker cluster label (e.g. SPEAKER_00)
                    
                    try:
                        db_helper.insert_speaker_profile(
                            profile_id=new_profile_id,
                            original_id=local_spk,
                            display_name=display_name,
                            voiceprint_embedding=avg_emb.astype(np.float32).tobytes()
                        )
                        local_to_global_map[local_spk] = new_profile_id
                    except Exception as e:
                        sys.stderr.write(f"[Diarization Warning] Failed to insert profile: {e}\n")
                        local_to_global_map[local_spk] = local_spk

            # 5. Map transcription segments using maximum overlap
            for seg in segments:
                seg_start = seg["start"]
                seg_end = seg["end"]
                
                best_local_spk = "Speaker_00"
                max_overlap = 0.0
                
                for turn in speaker_timeline:
                    overlap_start = max(seg_start, turn["start"])
                    overlap_end = min(seg_end, turn["end"])
                    overlap_len = max(0.0, overlap_end - overlap_start)
                    
                    if overlap_len > max_overlap:
                        max_overlap = overlap_len
                        best_local_spk = turn["speaker"]
                
                # Assign the matched global speaker profile ID (or fallback)
                seg["speaker"] = local_to_global_map.get(best_local_spk, best_local_spk)
                
            return segments
        except Exception as e:
            sys.stderr.write(f"[Diarization Warning] Diarization execution error: {e}. Falling back to default speaker mapping.\n")
            return segments
