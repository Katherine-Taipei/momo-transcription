import os
import sys
import numpy as np
import soundfile as sf

class VadProcessor:
    def __init__(self):
        self.enabled = False
        self.session = None
        try:
            import onnxruntime as ort
            from huggingface_hub import hf_hub_download
            
            model_dir = os.path.join(os.path.dirname(os.path.dirname(__file__)), "models")
            local_model_path = os.path.join(model_dir, "silero_vad.onnx")
            
            if os.path.exists(local_model_path):
                model_path = local_model_path
            else:
                try:
                    model_path = hf_hub_download(
                        repo_id="snakers4/silero-vad",
                        filename="silero_vad.onnx",
                        local_dir=model_dir,
                        local_dir_use_symlinks=False
                    )
                except Exception as dl_err:
                    import shutil
                    import faster_whisper
                    fw_assets_path = os.path.join(os.path.dirname(faster_whisper.__file__), "assets", "silero_vad.onnx")
                    if os.path.exists(fw_assets_path):
                        os.makedirs(model_dir, exist_ok=True)
                        shutil.copy(fw_assets_path, local_model_path)
                        model_path = local_model_path
                        sys.stderr.write(f"[VAD Info] Copied silero_vad.onnx from faster-whisper assets to {local_model_path}\n")
                    else:
                        raise dl_err
            
            # Suppress ort verbose logging
            opts = ort.SessionOptions()
            opts.log_severity_level = 3
            self.session = ort.InferenceSession(model_path, sess_options=opts, providers=['CPUExecutionProvider'])
            self.enabled = True
        except Exception as e:
            sys.stderr.write(f"[VAD Warning] Failed to initialize Silero VAD: {e}. Falling back to disabled VAD.\n")
            self.enabled = False

    def process_audio(self, audio_path: str, output_dir: str) -> str:
        """
        Reads audio, detects active speech using Silero VAD, 
        mutes non-speech segments (fills them with zero), 
        and saves a new temp WAV file.
        Returns the path to the clean audio (original path if failed).
        """
        if not self.enabled or not self.session:
            return audio_path

        temp_output_path = os.path.join(output_dir, f"vad_temp_{os.path.basename(audio_path)}")
        try:
            # Read audio data
            data, sr = sf.read(audio_path)
            if len(data.shape) > 1:
                data = data.mean(axis=1) # Convert to mono
            
            # Audio must be float32 in [-1.0, 1.0]
            audio = data.astype(np.float32)
            
            # Silero VAD parameters
            sr_input = np.array([16000], dtype=np.int64)
            h = np.zeros((2, 1, 64), dtype=np.float32)
            c = np.zeros((2, 1, 64), dtype=np.float32)
            
            # Process in 512 sample chunks (32 ms)
            hop_size = 512
            speech_probs = []
            
            for i in range(0, len(audio), hop_size):
                chunk = audio[i:i+hop_size]
                if len(chunk) < hop_size:
                    # Pad last chunk with zeros
                    chunk = np.pad(chunk, (0, hop_size - len(chunk)))
                
                chunk_input = np.expand_dims(chunk, axis=0)
                
                inputs = {
                    "input": chunk_input,
                    "sr": sr_input,
                    "h": h,
                    "c": c
                }
                out, h_out, c_out = self.session.run(None, inputs)
                h, c = h_out, c_out
                prob = out[0][0] # probability of speech
                speech_probs.append(prob)
            
            # Classify segments as speech/non-speech
            threshold = 0.5
            is_speech = np.array(speech_probs) > threshold
            
            # Reconstruct clean audio
            clean_audio = np.zeros_like(audio)
            
            for idx, speech_active in enumerate(is_speech):
                start_sample = idx * hop_size
                end_sample = min(start_sample + hop_size, len(audio))
                if speech_active:
                    clean_audio[start_sample:end_sample] = audio[start_sample:end_sample]
            
            sf.write(temp_output_path, clean_audio, sr)
            return temp_output_path
            
        except Exception as e:
            sys.stderr.write(f"[VAD Warning] VAD processing error: {e}. Falling back to baseline audio.\n")
            if os.path.exists(temp_output_path):
                try: os.remove(temp_output_path)
                except: pass
            return audio_path
