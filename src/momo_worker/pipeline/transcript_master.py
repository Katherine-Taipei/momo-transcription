import os
import sys

class TranscriptMaster:
    def __init__(self):
        self.enabled = False
        self.whisperx = None
        try:
            import whisperx
            self.whisperx = whisperx
            self.enabled = True
        except Exception as e:
            sys.stderr.write(f"[Alignment Warning] Failed to initialize WhisperX Alignment: {e}. Falling back to disabled aligner.\n")
            self.enabled = False

    def process_alignment(self, audio_path: str, segments: list, language: str = "zh", device: str = "cpu") -> list:
        """
        Aligns transcription segments with audio using WhisperX.
        Returns a list of segments containing word-level timestamps in 'words' attribute.
        If alignment fails or is disabled, falls back to segment-level fallback word wrappers.
        """
        # Set default 'words' to prevent JSON schema validation failure
        for seg_idx, seg in enumerate(segments):
            if "words" not in seg or not seg["words"]:
                seg["words"] = [{
                    "word": seg["text"],
                    "start_time": seg["start"],
                    "end_time": seg["end"],
                    "confidence": 1.0
                }]
            if "segment_index" not in seg:
                seg["segment_index"] = seg_idx

        if not self.enabled or not self.whisperx:
            return segments

        try:
            import soundfile as sf
            audio_data, sr = sf.read(audio_path)
            if len(audio_data.shape) > 1:
                audio_data = audio_data.mean(axis=1)
            audio_data = audio_data.astype("float32")

            model_a, metadata = self.whisperx.load_align_model(language_code=language, device=device)
            
            align_input = [{"start": s["start"], "end": s["end"], "text": s["text"]} for s in segments]
            
            align_result = self.whisperx.align(
                align_input, 
                model_a, 
                metadata, 
                audio_data, 
                device, 
                return_char_alignments=False
            )
            
            aligned_segments = align_result.get("segments", [])
            for idx, aligned_seg in enumerate(aligned_segments):
                if idx < len(segments):
                    target_seg = segments[idx]
                    target_seg["start"] = aligned_seg.get("start", target_seg["start"])
                    target_seg["end"] = aligned_seg.get("end", target_seg["end"])
                    
                    words_list = []
                    for w in aligned_seg.get("words", []):
                        if "start" in w and "end" in w:
                            words_list.append({
                                "word": w["word"],
                                "start_time": w["start"],
                                "end_time": w["end"],
                                "confidence": w.get("score", 1.0)
                            })
                    
                    if words_list:
                        target_seg["words"] = words_list
                        
            return segments
        except Exception as e:
            sys.stderr.write(f"[Alignment Warning] WhisperX alignment error: {e}. Falling back to default word mapping.\n")
            return segments
