import os
import sys
import numpy as np
from faster_whisper import WhisperModel

class StreamingProcessor:
    def __init__(self):
        self._model = None
        self.model_size = "tiny"
        self.device = "cpu"
        self.quantization = "int8"
        self.cpu_threads = 4

    @property
    def model(self):
        if self._model is None:
            models_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "models"))
            os.makedirs(models_dir, exist_ok=True)
            self._model = WhisperModel(
                self.model_size,
                device=self.device,
                compute_type=self.quantization,
                cpu_threads=self.cpu_threads,
                download_root=models_dir
            )
        return self._model

    def transcribe_pcm(self, pcm_bytes: bytes) -> str:
        """
        Transcribe 16-bit 16kHz mono PCM bytes.
        """
        if not pcm_bytes:
            return ""

        # Convert 16-bit PCM bytes to float32 numpy array normalized to [-1.0, 1.0]
        audio_data = np.frombuffer(pcm_bytes, dtype=np.int16)
        if len(audio_data) == 0:
            return ""
            
        audio_float32 = audio_data.astype(np.float32) / 32768.0

        try:
            # Transcribe the numpy array directly
            segments, info = self.model.transcribe(audio_float32, beam_size=5)
            texts = [seg.text.strip() for seg in segments if seg.text.strip()]
            return " ".join(texts)
        except Exception as e:
            sys.stderr.write(f"[Streaming Error] Transcription failed: {e}\n")
            return ""
