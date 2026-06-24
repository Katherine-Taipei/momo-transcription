import os
import json
from faster_whisper import WhisperModel
from utils.db_helper import DbHelper

class WhisperWorker:
    def __init__(self, db_helper: DbHelper):
        self.db_helper = db_helper
        self._model = None
        self._current_config = None

    def _get_model(self, model_size: str, device: str, quantization: str, cpu_threads: int):
        config_key = f"{model_size}_{device}_{quantization}_{cpu_threads}"
        if self._model is None or self._current_config != config_key:
            # Save models locally to prevent downloading to home directories
            models_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "models")
            os.makedirs(models_dir, exist_ok=True)
            self._model = WhisperModel(
                model_size,
                device=device,
                compute_type=quantization,
                cpu_threads=cpu_threads,
                download_root=models_dir
            )
            self._current_config = config_key
        return self._model

    def transcribe_chunk(self, chunk_id: str, chunk_index: int, file_path: str, model_size: str, device: str, quantization: str, cpu_threads: int, temp_dir: str) -> dict:
        model = self._get_model(model_size, device, quantization, cpu_threads)
        
        segments, info = model.transcribe(file_path, beam_size=5)
        
        output_segments = []
        raw_text_parts = []
        
        for segment in segments:
            text = segment.text.strip()
            if text:
                output_segments.append({
                    "start": float(segment.start),
                    "end": float(segment.end),
                    "text": text,
                    "confidence": float(info.language_probability) if info else 1.0
                })
                raw_text_parts.append(text)

        raw_text = " ".join(raw_text_parts)
        
        result_payload = {
            "chunk_id": chunk_id,
            "chunk_index": chunk_index,
            "raw_text": raw_text,
            "segments": output_segments
        }
        
        json_file_path = os.path.join(temp_dir, f"chunk_index_{chunk_index:04d}.json")
        with open(json_file_path, "w", encoding="utf-8") as f:
            json.dump(result_payload, f, ensure_ascii=False, indent=2)
            
        self.db_helper.update_audio_chunk_status(chunk_id, "COMPLETED")
        
        return {
            "json_path": json_file_path,
            "raw_text": raw_text,
            "segments": output_segments
        }

    def normalize_text(self, text: str) -> str:
        if not text:
            return ""
        import unicodedata
        # strip and NFKC-normalize to unify full/half width and punctuation
        text = text.strip()
        normalized = unicodedata.normalize('NFKC', text)
        # remove punctuation/whitespace for deduplication comparison
        return "".join(c for c in normalized if c.isalnum()).lower().strip()

    def merge_segments(self, segments: list):
        """
        Deduplicates and merges segments from overlapping chunks.
        Yields segments as a generator to optimize memory footprint.
        """
        sorted_segs = sorted(segments, key=lambda x: x["start"])
        last = None
        for seg in sorted_segs:
            if last is None:
                last = dict(seg)
                continue
            
            last_norm = self.normalize_text(last["text"])
            seg_norm = self.normalize_text(seg["text"])
            
            if seg_norm == last_norm:
                # Skip exact duplicate text in the overlap zone
                continue
            
            last_chunk = last.get("chunk_index")
            seg_chunk = seg.get("chunk_index")
            
            # Only merge if they are from different chunks and overlap
            if last_chunk is not None and seg_chunk is not None and last_chunk != seg_chunk:
                if seg["start"] - last["end"] < 0.5:
                    last["end"] = max(last["end"], seg["end"])
                    last_text = last["text"].strip()
                    seg_text = seg["text"].strip()
                    if seg_text:
                        if last_text:
                            has_english = ord(last_text[-1]) < 128 or ord(seg_text[0]) < 128
                            last["text"] = last_text + (" " if has_english else "") + seg_text
                        else:
                            last["text"] = seg_text
                    if "words" in last and "words" in seg:
                        last["words"].extend(seg["words"])
                else:
                    yield last
                    last = dict(seg)
            else:
                # Different sentences within the same chunk, do NOT merge
                yield last
                last = dict(seg)
                
        if last is not None:
            yield last

    def merge_words_to_sentences(self, segments: list) -> list:
        """
        Reconstructs segment boundaries from aligned words.
        Splits sentences on punctuation, speaker transition, or length constraints (6s or 120 chars).
        """
        all_words = []
        for seg in segments:
            speaker = seg.get("speaker", "Speaker_00")
            words = seg.get("words", [])
            if not words:
                words = [{
                    "word": seg["text"],
                    "start": seg["start"],
                    "end": seg["end"],
                    "score": seg.get("confidence", 1.0)
                }]
            for w in words:
                word_item = dict(w)
                word_item["speaker"] = speaker
                all_words.append(word_item)
                
        if not all_words:
            return segments
            
        new_segments = []
        current_words = []
        sentence_start_time = None
        delimiters = {"。", "！", "？", "；", "…", ".", "?", "!", ";"}
        
        for idx, w in enumerate(all_words):
            word_text = w.get("word", "").strip()
            word_start = w.get("start_time", w.get("start", 0.0))
            word_end = w.get("end_time", w.get("end", 0.0))
            
            if sentence_start_time is None:
                sentence_start_time = word_start
                
            current_words.append(w)
            
            has_punctuation = any(char in word_text for char in delimiters)
            duration = word_end - sentence_start_time
            temp_text = "".join(x.get("word", "").strip() for x in current_words)
            length_exceeded = len(temp_text) > 120
            
            next_speaker_change = False
            if idx + 1 < len(all_words):
                next_speaker_change = all_words[idx + 1]["speaker"] != w["speaker"]
                
            if has_punctuation or duration >= 6.0 or length_exceeded or next_speaker_change:
                new_segments.append(self._create_segment_from_words(current_words, sentence_start_time, word_end))
                current_words = []
                sentence_start_time = None
                
        if current_words:
            last_end = current_words[-1].get("end_time", current_words[-1].get("end", 0.0))
            new_segments.append(self._create_segment_from_words(current_words, sentence_start_time, last_end))
            
        return new_segments

    def _create_segment_from_words(self, words: list, start: float, end: float) -> dict:
        text_parts = []
        for idx, w in enumerate(words):
            w_text = w.get("word", "").strip()
            if not w_text:
                continue
            if idx > 0:
                last_text = words[idx - 1].get("word", "").strip()
                if (last_text and ord(last_text[-1]) < 128) or (w_text and ord(w_text[0]) < 128):
                    text_parts.append(" ")
            text_parts.append(w_text)
            
        text = "".join(text_parts)
        speaker = words[0].get("speaker", "Speaker_00")
        conf = sum(w.get("score", w.get("confidence", 1.0)) for w in words) / len(words)
        
        mapped_words = []
        for w in words:
            mapped_words.append({
                "word": w.get("word", ""),
                "start_time": w.get("start_time", w.get("start", start)),
                "end_time": w.get("end_time", w.get("end", end)),
                "confidence": w.get("score", w.get("confidence", 1.0))
            })
            
        return {
            "start": start,
            "end": end,
            "text": text,
            "speaker": speaker,
            "confidence": conf,
            "words": mapped_words
        }
