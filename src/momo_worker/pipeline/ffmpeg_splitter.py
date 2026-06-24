import subprocess
import os
import glob
import shutil
from utils.db_helper import DbHelper

class FfmpegSplitter:
    def __init__(self, db_helper: DbHelper):
        self.db_helper = db_helper
        self.ffmpeg_exe = "ffmpeg"
        self.ffprobe_exe = "ffprobe"
        self._resolve_binaries()

    def _resolve_binaries(self):
        # Resolve global binaries
        if shutil.which("ffmpeg") and shutil.which("ffprobe"):
            self.ffmpeg_exe = "ffmpeg"
            self.ffprobe_exe = "ffprobe"
            return
            
        # Fallback to winget default paths on Windows
        winget_ffmpeg_dir = r"C:\Users\User\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin"
        ffmpeg_path = os.path.join(winget_ffmpeg_dir, "ffmpeg.exe")
        ffprobe_path = os.path.join(winget_ffmpeg_dir, "ffprobe.exe")
        
        if os.path.exists(ffmpeg_path) and os.path.exists(ffprobe_path):
            self.ffmpeg_exe = ffmpeg_path
            self.ffprobe_exe = ffprobe_path

    def get_audio_duration(self, file_path: str) -> float:
        cmd = [
            self.ffprobe_exe,
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            file_path
        ]
        try:
            result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=True)
            return float(result.stdout.strip())
        except Exception as e:
            return 0.0

    def split_audio(self, media_file_id: str, file_path: str, output_dir: str, segment_length_sec: int = 300) -> list:
        os.makedirs(output_dir, exist_ok=True)
        duration = self.get_audio_duration(file_path)
        
        overlap = 2.0
        step = segment_length_sec - overlap
        
        chunks_info = []
        # Find all full segments
        current_start = 0.0
        while current_start + segment_length_sec <= duration:
            chunks_info.append({
                "start": current_start,
                "duration": float(segment_length_sec)
            })
            current_start += step
            
        # If the duration is not fully covered (or if we have no chunks at all)
        if not chunks_info:
            chunks_info.append({
                "start": 0.0,
                "duration": duration
            })
        else:
            last_end = chunks_info[-1]["start"] + chunks_info[-1]["duration"]
            if last_end < duration:
                final_start = max(0.0, last_end - overlap)
                chunks_info.append({
                    "start": final_start,
                    "duration": duration - final_start
                })
        
        chunks_metadata = []
        for idx, info in enumerate(chunks_info):
            start_time = info["start"]
            chunk_duration = info["duration"]
            chunk_id = f"{media_file_id}_c{idx}"
            chunk_file = os.path.join(output_dir, f"chunk_{idx:04d}.wav")
            
            cmd = [
                self.ffmpeg_exe,
                "-y",
                "-ss", f"{start_time:.3f}",
                "-t", f"{chunk_duration:.3f}",
                "-i", file_path,
                "-af", f"apad=whole_dur={chunk_duration:.3f}",
                "-avoid_negative_ts", "1",
                "-c:a", "pcm_s16le",
                "-ar", "16000",
                "-ac", "1",
                chunk_file
            ]
            
            subprocess.run(cmd, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            
            end_time = start_time + chunk_duration
            if duration > 0 and end_time > duration:
                end_time = duration
            
            self.db_helper.insert_audio_chunk(
                chunk_id=chunk_id,
                media_file_id=media_file_id,
                chunk_index=idx,
                start_time=start_time,
                end_time=end_time,
                file_path=chunk_file
            )
            
            chunks_metadata.append({
                "chunk_id": chunk_id,
                "chunk_index": idx,
                "file_path": chunk_file,
                "start_time": start_time,
                "end_time": end_time
            })
            
        return chunks_metadata
