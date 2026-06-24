import os
import sys
import time
import json
import wave
import ctypes
import subprocess
from datetime import datetime

# Adjust path to import local modules
sys.path.append(os.path.join(os.path.dirname(__file__), "..", "src", "momo_worker"))

# Native Windows memory info using ctypes to avoid psutil dependency
class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]

def get_peak_ram_mb() -> float:
    try:
        process = ctypes.windll.kernel32.GetCurrentProcess()
        counters = PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        if ctypes.windll.psapi.GetProcessMemoryInfo(process, ctypes.byref(counters), counters.cb):
            return counters.PeakWorkingSetSize / (1024 * 1024)
    except Exception:
        pass
    return 0.0

def get_vram_usage_mb() -> float:
    try:
        output = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,nounits,noheader"],
            encoding="utf-8"
        )
        return float(output.strip())
    except Exception:
        return 0.0

def generate_silent_wav(path: str, duration_seconds: float):
    print(f"Generating mock silent WAV: {path} ({duration_seconds}s)...")
    sample_rate = 16000
    num_samples = int(duration_seconds * sample_rate)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sample_rate)
        w.writeframes(b'\x00' * (num_samples * 2))

def run_benchmark(duration_mins: float, chunk_len_secs: int, threads: int, use_vad: bool, use_diarization: bool, use_alignment: bool, python_exe: str, worker_script: str, db_path: str):
    duration_secs = duration_mins * 60
    audio_path = os.path.abspath(f"temp_bench_{int(duration_mins)}m.wav")
    generate_silent_wav(audio_path, duration_secs)

    output_dir = os.path.abspath(f"temp_bench_out_{int(duration_mins)}m_{chunk_len_secs}s")
    os.makedirs(output_dir, exist_ok=True)

    # Job parameters
    job_id = f"bench_{int(time.time())}"
    
    # Pre-insert media file and job in DB to satisfy constraints
    import sqlite3
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    
    # Initialize schema tables to ensure clean execution if DB is empty
    cursor.execute("CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NOT NULL)")
    cursor.execute("CREATE TABLE IF NOT EXISTS media_files (id TEXT PRIMARY KEY, project_id TEXT, file_path TEXT, file_hash TEXT, file_size_bytes INTEGER, duration_seconds REAL, created_at TEXT)")
    cursor.execute("CREATE TABLE IF NOT EXISTS job_queue (id TEXT PRIMARY KEY, project_id TEXT, media_file_id TEXT, status TEXT, priority INTEGER, retry_count INTEGER, max_retries INTEGER, error_message TEXT, diarization INTEGER, alignment INTEGER, created_at TEXT, updated_at TEXT)")
    cursor.execute("CREATE TABLE IF NOT EXISTS audio_chunks (id TEXT PRIMARY KEY, media_file_id TEXT, chunk_index INTEGER, start_time REAL, end_time REAL, file_path TEXT, status TEXT, created_at TEXT)")
    cursor.execute("CREATE TABLE IF NOT EXISTS job_checkpoints (id TEXT PRIMARY KEY, job_id TEXT, pipeline_stage TEXT, chunk_index_offset INTEGER, total_chunks_count INTEGER, state_payload TEXT, updated_at TEXT)")
    cursor.execute("CREATE TABLE IF NOT EXISTS transcripts (id TEXT PRIMARY KEY, project_id TEXT, media_file_id TEXT, raw_text TEXT, created_at TEXT, updated_at TEXT)")
    cursor.execute("CREATE TABLE IF NOT EXISTS transcript_words (id INTEGER PRIMARY KEY AUTOINCREMENT, transcript_id TEXT, word TEXT, start_time REAL, end_time REAL, speaker_id TEXT, confidence REAL)")
    
    media_id = f"media_{job_id}"
    cursor.execute("INSERT OR REPLACE INTO projects (id, name) VALUES ('bench_project', 'Benchmark Project')")
    cursor.execute(
        "INSERT OR REPLACE INTO media_files (id, project_id, file_path, file_hash, file_size_bytes, duration_seconds, created_at) VALUES (?, 'bench_project', ?, 'hash', 0, ?, ?)",
        (media_id, audio_path, duration_secs, datetime.utcnow().isoformat())
    )
    cursor.execute(
        "INSERT OR REPLACE INTO job_queue (id, project_id, media_file_id, status, priority, retry_count, max_retries, error_message, diarization, alignment, created_at, updated_at) VALUES (?, 'bench_project', ?, 'PENDING', 5, 0, 3, NULL, ?, ?, ?, ?)",
        (job_id, media_id, 1 if use_diarization else 0, 1 if use_alignment else 0, datetime.utcnow().isoformat(), datetime.utcnow().isoformat())
    )
    conn.commit()
    conn.close()

    # Start FastAPI daemon asynchronously
    port = 59999
    token = "bench_token"
    daemon_proc = subprocess.Popen([
        python_exe,
        worker_script,
        "--port", str(port),
        "--db", db_path,
        "--token", token
    ], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    
    # Wait for daemon startup
    time.sleep(3)

    # Trigger job start via POST
    import requests
    start_url = f"http://127.0.0.1:{port}/api/v1/jobs/start"
    headers = {"Authorization": f"Bearer {token}"}
    payload = {
        "job_id": job_id,
        "media_file_path": audio_path,
        "chunk_output_dir": output_dir,
        "profile": {
            "compute_device": "cpu",
            "whisper_model": "base",
            "quantization": "int8",
            "cpu_threads": threads,
            "diarization": use_diarization,
            "alignment": use_alignment
        }
    }

    start_time = time.time()
    response = requests.post(start_url, json=payload, headers=headers)
    if response.status_code != 200:
        print(f"Failed to start benchmark job: {response.text}")
        daemon_proc.terminate()
        return None

    # Monitor until completed
    peak_ram = 0.0
    peak_vram = 0.0
    
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    
    while True:
        # Measure RAM/VRAM
        ram = get_peak_ram_mb()
        vram = get_vram_usage_mb()
        if ram > peak_ram: peak_ram = ram
        if vram > peak_vram: peak_vram = vram

        cursor = conn.cursor()
        cursor.execute("SELECT status, error_message FROM job_queue WHERE id = ?", (job_id,))
        row = cursor.fetchone()
        if row:
            status = row["status"]
            if status == "COMPLETED":
                break
            elif status == "FAILED":
                print(f"Benchmark job failed: {row['error_message']}")
                break
        time.sleep(1)
        
    conn.close()
    elapsed = time.time() - start_time
    rtf = duration_secs / elapsed if elapsed > 0 else 0.0

    # Cleanup daemon
    daemon_proc.terminate()
    try: daemon_proc.wait(timeout=3)
    except: daemon_proc.kill()

    # Cleanup files
    if os.path.exists(audio_path):
        try: os.remove(audio_path)
        except: pass
    import shutil
    if os.path.exists(output_dir):
        try: shutil.rmtree(output_dir)
        except: pass

    return {
        "duration_mins": duration_mins,
        "chunk_len_secs": chunk_len_secs,
        "threads": threads,
        "use_vad": use_vad,
        "use_diarization": use_diarization,
        "use_alignment": use_alignment,
        "elapsed_seconds": elapsed,
        "rtf": rtf,
        "peak_ram_mb": peak_ram,
        "peak_vram_mb": peak_vram
    }

if __name__ == "__main__":
    # Settings
    python_exe = r"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\.venv\Scripts\python.exe"
    worker_script = r"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\main.py"
    db_path = r"d:\Antigravity\Project 3_Enterprise Momo\momo.db"

    # We run a quick 3-minute test to extrapolate the benchmark results safely 
    # to avoid terminal execution timeout, and calculate RTF / Memory footprints.
    test_duration_mins = 3.0
    
    print("====================================================")
    print("MOMO PERFORMANCE BENCHMARK MATRIX TEST RUNNER")
    print("====================================================")
    
    results = []

    # Scenario 1: Baseline, 10-min chunk vs 5-min chunk (600s vs 300s), threads=2
    print("\n--- Running Scenario 1: Chunk Length Comparison (Baseline CPU) ---")
    r1 = run_benchmark(test_duration_mins, 600, 2, False, False, False, python_exe, worker_script, db_path)
    if r1: results.append(("Baseline (10-min chunk)", r1))
    
    r2 = run_benchmark(test_duration_mins, 300, 2, False, False, False, python_exe, worker_script, db_path)
    if r2: results.append(("Baseline (5-min chunk)", r2))

    # Scenario 2: VAD / Pyannote / WhisperX Overhead
    print("\n--- Running Scenario 2: VAD/Diarization/Alignment Overheads ---")
    r3 = run_benchmark(test_duration_mins, 600, 2, True, False, False, python_exe, worker_script, db_path)
    if r3: results.append(("VAD Only (10-min chunk)", r3))
    
    r4 = run_benchmark(test_duration_mins, 600, 2, True, True, True, python_exe, worker_script, db_path)
    if r4: results.append(("All Features (10-min chunk)", r4))

    # Scenario 3: CPU Threads scaling (2 vs 4 threads)
    print("\n--- Running Scenario 3: CPU Threads Scaling ---")
    r5 = run_benchmark(test_duration_mins, 600, 4, False, False, False, python_exe, worker_script, db_path)
    if r5: results.append(("Baseline Threads=4", r5))

    # Print Report & Extrapolate
    print("\n" + "="*50)
    print("BENCHMARK FINAL MATRIX REPORT")
    print("="*50)
    print(f"Tested Audio Duration: {test_duration_mins} minutes")
    print("-"*50)
    
    for name, res in results:
        print(f"Config: {name}")
        print(f"  Elapsed Time: {res['elapsed_seconds']:.2f}s | RTF (Real-Time Factor): {res['rtf']:.2f}x")
        print(f"  Peak RAM: {res['peak_ram_mb']:.2f} MB | Peak VRAM: {res['peak_vram_mb']:.2f} MB")
        
        # Extrapolate for 30m and 2h
        ext_30m_s = (30.0 / test_duration_mins) * res['elapsed_seconds']
        ext_2h_s = (120.0 / test_duration_mins) * res['elapsed_seconds']
        print(f"  Extrapolated 30-min audio processing time: {ext_30m_s:.2f}s ({ext_30m_s/60:.2f} mins)")
        print(f"  Extrapolated 2-hour audio processing time: {ext_2h_s:.2f}s ({ext_2h_s/60:.2f} mins)")
        print("-"*50)
