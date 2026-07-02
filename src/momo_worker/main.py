import argparse
import asyncio
import os
import sys
import json
import uvicorn
from fastapi import FastAPI, Depends, HTTPException, WebSocket, WebSocketDisconnect, BackgroundTasks, Response
from fastapi.security import HTTPBearer, HTTPAuthorizationCredentials
from typing import List, Dict, Optional
from pydantic import BaseModel

# Setup DLL search path for av.libs on Windows to prevent DLL load failures under dotnet host
if sys.platform == "win32":
    av_libs_paths = []
    # Relative to python.exe
    py_dir = os.path.dirname(sys.executable)
    av_libs_paths.append(os.path.abspath(os.path.join(py_dir, "..", "Lib", "site-packages", "av.libs")))
    # Via site packages
    try:
        import site
        for site_dir in site.getsitepackages():
            av_libs_paths.append(os.path.join(site_dir, "av.libs"))
    except Exception:
        pass
    
    for av_libs in av_libs_paths:
        if os.path.isdir(av_libs):
            os.environ["PATH"] = av_libs + os.pathsep + os.environ.get("PATH", "")
            if hasattr(os, "add_dll_directory"):
                try:
                    os.add_dll_directory(av_libs)
                except Exception:
                    pass
            break

# Adjust path to import local modules
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from config import JobRequest
from utils.db_helper import DbHelper
from pipeline.ffmpeg_splitter import FfmpegSplitter
from pipeline.vad_processor import VadProcessor
from pipeline.glossary_processor import GlossaryProcessor

try:
    from pipeline.whisper_worker import WhisperWorker
except Exception as e:
    sys.stderr.write(f"[Whisper Warning] Failed to load WhisperWorker: {e}\n")
    WhisperWorker = None

try:
    from pipeline.diarizer import Diarizer
except Exception as e:
    sys.stderr.write(f"[Diarizer Warning] Failed to load Diarizer: {e}\n")
    Diarizer = None

try:
    from pipeline.transcript_master import TranscriptMaster
except Exception as e:
    sys.stderr.write(f"[TranscriptMaster Warning] Failed to load TranscriptMaster: {e}\n")
    TranscriptMaster = None

try:
    from pipeline.rag_orchestrator import RagOrchestrator
except Exception as e:
    sys.stderr.write(f"[RAG Warning] Failed to load RagOrchestrator: {e}\n")
    RagOrchestrator = None

try:
    from pipeline.streaming_server import StreamingProcessor
except Exception as e:
    sys.stderr.write(f"[Streaming Warning] Failed to load StreamingProcessor: {e}\n")
    StreamingProcessor = None

# Argument parsing
is_testing = "unittest" in sys.argv[0] or any("unittest" in arg for arg in sys.argv)
parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=5000)
parser.add_argument("--db", type=str, required=not is_testing)
parser.add_argument("--token", type=str, required=not is_testing)
args, unknown = parser.parse_known_args()
if is_testing:
    if not args.db:
        args.db = "test_momo.db"
    if not args.token:
        args.token = "test_token"

app = FastAPI(title="Momo Pipeline Worker Daemon")
security = HTTPBearer()

db_helper = DbHelper(args.db)
ffmpeg_splitter = FfmpegSplitter(db_helper)
whisper_worker = WhisperWorker(db_helper) if WhisperWorker else None
vad_processor = VadProcessor()
diarizer = Diarizer() if Diarizer else None
transcript_master = TranscriptMaster() if TranscriptMaster else None
glossaries_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "glossaries"))
glossary_processor = GlossaryProcessor(glossaries_dir)
rag_orchestrator = RagOrchestrator() if RagOrchestrator else None
streaming_processor = StreamingProcessor() if StreamingProcessor else None

# Active execution states
active_websockets: List[WebSocket] = []
cancel_flags: Dict[str, bool] = {}
pause_flags: Dict[str, bool] = {}

def verify_token(credentials: HTTPAuthorizationCredentials = Depends(security)):
    if credentials.credentials != args.token:
        raise HTTPException(status_code=401, detail="Unauthorized token.")
    return credentials.credentials

async def broadcast_progress(job_id: str, stage: str, progress: float, current_chunk: int, total_chunks: int):
    payload = {
        "job_id": job_id,
        "stage": stage,
        "progress_percentage": float(progress),
        "current_chunk": int(current_chunk),
        "total_chunks": int(total_chunks),
        "throughput_rt": 1.0
    }
    raw_frame = json.dumps(payload)
    # Create copies of active connections to prevent mutations during transmission
    connections = list(active_websockets)
    for ws in connections:
        try:
            await ws.send_text(raw_frame)
        except Exception:
            if ws in active_websockets:
                active_websockets.remove(ws)

async def run_transcription_pipeline(job_id: str, request: JobRequest):
    media_file_id = db_helper.get_media_file_id_by_job(job_id)
    project_id = db_helper.get_project_id_by_job(job_id)
    job_settings = db_helper.get_job_settings(job_id)
    selected_glossaries = job_settings.get("selected_glossaries", [])
    
    if whisper_worker is None:
        db_helper.update_job_status(job_id, "FAILED", "WhisperWorker is unavailable (failed to load torch/ctranslate2).")
        await broadcast_progress(job_id, "FAILED", 0.0, 0, 0)
        return

    cancel_flags[job_id] = False
    pause_flags[job_id] = False
    
    try:
        # Query existing checkpoints for WHISPER_TRANSCRIPTION
        checkpoint = db_helper.get_checkpoint(job_id, "WHISPER_TRANSCRIPTION")
        
        output_dir = request.chunk_output_dir
        os.makedirs(output_dir, exist_ok=True)
        
        chunks = []
        if checkpoint:
            # Resume existing job
            total_chunks = checkpoint["total_chunks"]
            chunk_offset = checkpoint["chunk_offset"]
            
            # Fetch already populated chunks
            conn = db_helper._get_connection()
            cursor = conn.cursor()
            cursor.execute(
                "SELECT id, chunk_index, file_path, start_time, end_time, status FROM audio_chunks WHERE media_file_id = ? ORDER BY chunk_index",
                (media_file_id,)
            )
            rows = cursor.fetchall()
            for r in rows:
                chunks.append({
                    "chunk_id": r["id"],
                    "chunk_index": r["chunk_index"],
                    "file_path": r["file_path"],
                    "start_time": r["start_time"],
                    "end_time": r["end_time"],
                    "status": r["status"]
                })
        else:
            # Fresh start: FFmpeg Split stage
            db_helper.update_checkpoint(job_id, "FFMPEG_SPLIT", 0, 0, {})
            await broadcast_progress(job_id, "FFMPEG_SPLIT", 10.0, 0, 1)
            
            # Split audio in separate thread
            chunks_meta = await asyncio.to_thread(
                ffmpeg_splitter.split_audio,
                media_file_id,
                request.media_file_path,
                output_dir,
                request.profile.chunk_length
            )
            for c in chunks_meta:
                chunks.append({
                    "chunk_id": c["chunk_id"],
                    "chunk_index": c["chunk_index"],
                    "file_path": c["file_path"],
                    "start_time": c["start_time"],
                    "end_time": c["end_time"],
                    "status": "PENDING"
                })
            
            total_chunks = len(chunks)
            chunk_offset = 0
            db_helper.update_checkpoint(job_id, "WHISPER_TRANSCRIPTION", 0, total_chunks, {})

        db_helper.update_job_status(job_id, "RUNNING")
        
        transcribed_segments = []
        raw_text_list = []
        
        for idx, chunk in enumerate(chunks):
            # Check runtime flags
            if cancel_flags.get(job_id):
                db_helper.update_job_status(job_id, "CANCELLED")
                await broadcast_progress(job_id, "CANCELLED", 100.0, idx, total_chunks)
                return
            if pause_flags.get(job_id):
                db_helper.update_job_status(job_id, "PAUSED")
                await broadcast_progress(job_id, "PAUSED", ((idx) / total_chunks) * 100.0, idx, total_chunks)
                return
                
            chunk_id = chunk["chunk_id"]
            chunk_index = chunk["chunk_index"]
            
            # Read from cache if already completed
            json_path = os.path.join(output_dir, f"chunk_index_{chunk_index:04d}.json")
            if chunk.get("status") == "COMPLETED" or os.path.exists(json_path):
                if os.path.exists(json_path):
                    with open(json_path, "r", encoding="utf-8") as f:
                        payload = json.load(f)
                        for seg in payload["segments"]:
                            seg["chunk_index"] = chunk_index
                            transcribed_segments.append(seg)
                        raw_text_list.append(payload["raw_text"])
                    continue
            
            # Execute VAD silence filter before transcription
            transcribe_audio_path = chunk["file_path"]
            is_vad_temp = False
            try:
                transcribe_audio_path = vad_processor.process_audio(chunk["file_path"], output_dir)
                if transcribe_audio_path != chunk["file_path"]:
                    is_vad_temp = True
            except Exception as e:
                sys.stderr.write(f"[VAD Warning] Failed during VAD execution: {e}\n")
                transcribe_audio_path = chunk["file_path"]

            # Execute transcription (CPU intensive blocking model run in thread)
            result = await asyncio.to_thread(
                whisper_worker.transcribe_chunk,
                chunk_id,
                chunk_index,
                transcribe_audio_path,
                request.profile.whisper_model,
                request.profile.compute_device,
                request.profile.quantization,
                request.profile.cpu_threads,
                output_dir
            )
            
            # Clean up temp VAD audio buffer
            if is_vad_temp and os.path.exists(transcribe_audio_path):
                try: os.remove(transcribe_audio_path)
                except: pass

            # Add offset times relative to overall media start
            chunk_offset_time = chunk["start_time"]
            for seg in result["segments"]:
                seg["start"] += chunk_offset_time
                seg["end"] += chunk_offset_time
                seg["chunk_index"] = chunk_index
                transcribed_segments.append(seg)
                
            raw_text_list.append(result["raw_text"])
            
            # Update checkpoint offset
            current_progress = ((idx + 1) / total_chunks) * 100.0
            db_helper.update_checkpoint(job_id, "WHISPER_TRANSCRIPTION", idx + 1, total_chunks, {})
            await broadcast_progress(job_id, "WHISPER_TRANSCRIPTION", current_progress, idx + 1, total_chunks)

        # De-duplicate and merge overlap segments
        transcribed_segments = list(whisper_worker.merge_segments(transcribed_segments))

        # Apply Speaker Diarization if requested
        if request.profile.diarization:
            await broadcast_progress(job_id, "PYANNOTE_DIARIZATION", 0.0, total_chunks, total_chunks)
            try:
                transcribed_segments = await asyncio.to_thread(
                    diarizer.process_diarization,
                    request.media_file_path,
                    transcribed_segments,
                    db_helper
                )
            except Exception as e:
                sys.stderr.write(f"[Diarization Warning] Pyannote failed: {e}\n")

        # Apply Forced Alignment if requested
        if request.profile.alignment:
            await broadcast_progress(job_id, "WHISPERX_ALIGNMENT", 0.0, total_chunks, total_chunks)
            try:
                transcribed_segments = await asyncio.to_thread(
                    transcript_master.process_alignment,
                    request.media_file_path,
                    transcribed_segments,
                    "zh",
                    request.profile.compute_device
                )
                # Word-level sentence splitting & merging (句幅合併)
                transcribed_segments = whisper_worker.merge_words_to_sentences(transcribed_segments)
            except Exception as e:
                sys.stderr.write(f"[Alignment Warning] WhisperX failed: {e}\n")

        # Apply Domain Glossary replacements if glossaries are selected
        if selected_glossaries:
            for seg in transcribed_segments:
                seg["text"] = glossary_processor.process_text(seg["text"], selected_glossaries)
                if "words" in seg:
                    for w in seg["words"]:
                        w["word"] = glossary_processor.process_text(w["word"], selected_glossaries)

        final_raw_text = " ".join(seg["text"] for seg in transcribed_segments)
        
        # Insert raw final transcripts
        db_helper.insert_transcript_master(
            project_id=project_id,
            media_file_id=media_file_id,
            raw_text=final_raw_text,
            segments=transcribed_segments
        )
        
        # Save transcript master JSON
        master_json_path = os.path.join(output_dir, "transcript_master.json")
        with open(master_json_path, "w", encoding="utf-8") as f:
            json.dump({
                "media_metadata": {
                    "file_hash": "",
                    "duration_seconds": 0.0
                },
                "segments": transcribed_segments
            }, f, ensure_ascii=False, indent=2)
            
        db_helper.update_job_status(job_id, "COMPLETED")
        await broadcast_progress(job_id, "COMPLETED", 100.0, total_chunks, total_chunks)
        
    except Exception as e:
        db_helper.update_job_status(job_id, "FAILED", str(e))
        sys.stderr.write(f"Pipeline failure: {str(e)}\n")
        await broadcast_progress(job_id, "FAILED", 0.0, 0, 0)

@app.post("/api/v1/jobs/start")
def start_job(request: JobRequest, background_tasks: BackgroundTasks, token: str = Depends(verify_token)):
    status = db_helper.get_job_status(request.job_id)
    if status in ("COMPLETED", "CANCELLED"):
        raise HTTPException(status_code=400, detail=f"Job is already in {status} state.")
        
    background_tasks.add_task(run_transcription_pipeline, request.job_id, request)
    return {"status": "ACCEPTED"}

@app.post("/api/v1/jobs/pause")
def pause_job(token: str = Depends(verify_token)):
    for job_id in list(cancel_flags.keys()):
        pause_flags[job_id] = True
    return {"status": "PAUSE_REQUESTED"}

@app.post("/api/v1/jobs/cancel")
def cancel_job(token: str = Depends(verify_token)):
    for job_id in list(cancel_flags.keys()):
        cancel_flags[job_id] = True
    return {"status": "CANCEL_REQUESTED"}

@app.get("/api/v1/status")
def get_status(token: str = Depends(verify_token)):
    return {
        "whisper": whisper_worker is not None,
        "diarizer": diarizer is not None,
        "rag": rag_orchestrator is not None,
        "streaming": streaming_processor is not None
    }

# Pydantic models for Revisions
class RevisionCreateRequest(BaseModel):
    transcript_id: str
    created_by: str
    description: Optional[str] = None

# Pydantic models for Comments & Tasks
class CommentCreateRequest(BaseModel):
    transcript_id: str
    paragraph_id: str
    author: str
    text: str
    parent_id: Optional[str] = None

class CommentUpdateRequest(BaseModel):
    text: Optional[str] = None
    status: Optional[str] = None

class TaskCreateRequest(BaseModel):
    transcript_id: str
    paragraph_id: str
    assignee: str
    author: str
    text: str

class TaskUpdateRequest(BaseModel):
    status: str

@app.post("/api/v1/comments", status_code=201)
def create_comment(request: CommentCreateRequest, response: Response, token: str = Depends(verify_token)):
    import uuid
    comment_id = str(uuid.uuid4())
    res = db_helper.insert_comment(
        comment_id=comment_id,
        transcript_id=request.transcript_id,
        paragraph_id=request.paragraph_id,
        author=request.author,
        text=request.text,
        parent_id=request.parent_id
    )
    response.headers["Location"] = f"/api/v1/comments/{comment_id}"
    return res

@app.get("/api/v1/comments/{comment_id}")
def get_comment(comment_id: str, token: str = Depends(verify_token)):
    comment = db_helper.get_comment(comment_id)
    if not comment:
        raise HTTPException(status_code=404, detail="Comment not found.")
    return comment

@app.patch("/api/v1/comments/{comment_id}")
def update_comment(comment_id: str, request: CommentUpdateRequest, token: str = Depends(verify_token)):
    existing = db_helper.get_comment(comment_id)
    if not existing:
        raise HTTPException(status_code=404, detail="Comment not found.")
    res = db_helper.update_comment(
        comment_id=comment_id,
        text=request.text,
        status=request.status
    )
    return res

@app.get("/api/v1/transcripts/{transcript_id}/comments")
def get_transcript_comments(transcript_id: str, token: str = Depends(verify_token)):
    return db_helper.get_comments_by_transcript(transcript_id)

@app.post("/api/v1/revisions", status_code=201)
def create_revision(request: RevisionCreateRequest, response: Response, token: str = Depends(verify_token)):
    import uuid
    transcript = db_helper.get_transcript(request.transcript_id)
    if not transcript:
        raise HTTPException(status_code=404, detail="Transcript not found.")
    
    revision_id = str(uuid.uuid4())
    version_number = db_helper.get_next_version_number(request.transcript_id)
    snapshot_text = transcript.get("raw_text", "")
    
    res = db_helper.insert_revision(
        revision_id=revision_id,
        transcript_id=request.transcript_id,
        version_number=version_number,
        created_by=request.created_by,
        snapshot_text=snapshot_text,
        description=request.description
    )
    response.headers["Location"] = f"/api/v1/revisions/{revision_id}"
    return res

@app.get("/api/v1/transcripts/{transcript_id}/revisions")
def get_transcript_revisions(transcript_id: str, token: str = Depends(verify_token)):
    transcript = db_helper.get_transcript(transcript_id)
    if not transcript:
        raise HTTPException(status_code=404, detail="Transcript not found.")
    return db_helper.get_revisions_by_transcript(transcript_id)

@app.get("/api/v1/revisions/{revision_id}")
def get_revision(revision_id: str, token: str = Depends(verify_token)):
    revision = db_helper.get_revision(revision_id)
    if not revision:
        raise HTTPException(status_code=404, detail="Revision not found.")
    return revision

@app.post("/api/v1/tasks", status_code=201)
def create_task(request: TaskCreateRequest, response: Response, token: str = Depends(verify_token)):
    import uuid
    task_id = str(uuid.uuid4())
    res = db_helper.insert_task(
        task_id=task_id,
        transcript_id=request.transcript_id,
        paragraph_id=request.paragraph_id,
        assignee=request.assignee,
        author=request.author,
        text=request.text
    )
    response.headers["Location"] = f"/api/v1/tasks/{task_id}"
    return res

@app.get("/api/v1/tasks/{task_id}")
def get_task(task_id: str, token: str = Depends(verify_token)):
    task = db_helper.get_task(task_id)
    if not task:
        raise HTTPException(status_code=404, detail="Task not found.")
    return task

@app.patch("/api/v1/tasks/{task_id}")
def update_task(task_id: str, request: TaskUpdateRequest, token: str = Depends(verify_token)):
    existing = db_helper.get_task(task_id)
    if not existing:
        raise HTTPException(status_code=404, detail="Task not found.")
    res = db_helper.update_task_status(task_id, request.status)
    return res

@app.get("/api/v1/transcripts/{transcript_id}/tasks")
def get_transcript_tasks(transcript_id: str, token: str = Depends(verify_token)):
    return db_helper.get_tasks_by_transcript(transcript_id)

# Pydantic models for RAG
class IngestSegment(BaseModel):
    start: float
    end: float
    text: str
    speaker: str = "Unknown"

class IngestRequest(BaseModel):
    project_id: str
    media_file_id: str
    segments: List[IngestSegment]

class QueryRequest(BaseModel):
    project_id: str
    query: str
    limit: int = 5

@app.post("/rag/ingest")
def rag_ingest(request: IngestRequest, token: str = Depends(verify_token)):
    if rag_orchestrator is None:
        raise HTTPException(status_code=503, detail="RAG service is unavailable (failed to load torch/ctranslate2).")
    segments_dict = [seg.model_dump() if hasattr(seg, "model_dump") else seg.dict() for seg in request.segments]
    res = rag_orchestrator.ingest_segments(request.project_id, request.media_file_id, segments_dict)
    return res

@app.post("/rag/query")
def rag_query(request: QueryRequest, token: str = Depends(verify_token)):
    if rag_orchestrator is None:
        raise HTTPException(status_code=503, detail="RAG service is unavailable (failed to load torch/ctranslate2).")
    # 1. Hybrid Search (RRF of BM25 + Qdrant vectors)
    results = rag_orchestrator.hybrid_search(db_helper, request.project_id, request.query, limit=request.limit)
    
    # 2. Fetch external findings (PubMed & SEC)
    external_findings = []
    try:
        pubmed_pmids = rag_orchestrator.pubmed_client.search(request.query, limit=2)
        if pubmed_pmids:
            external_findings.extend(rag_orchestrator.pubmed_client.fetch_summaries(pubmed_pmids))
    except Exception:
        pass
        
    try:
        sec_filings = rag_orchestrator.sec_client.get_recent_filings(request.query, limit=2)
        if sec_filings:
            external_findings.extend(sec_filings)
    except Exception:
        pass
        
    # 3. Build Prompt
    prompt = rag_orchestrator.build_prompt(results, external_findings, request.query)
    
    # 4. Generate Report
    report = rag_orchestrator.generate_report(prompt)
    
    return {
        "results": results,
        "external_findings": external_findings,
        "report": report
    }

@app.websocket("/ws/live-stream")
async def live_stream_websocket(websocket: WebSocket, token: str):
    if token != args.token:
        await websocket.close(code=4001)
        return
        
    if streaming_processor is None:
        await websocket.accept()
        try:
            await websocket.send_json({
                "text": "Streaming service is unavailable (failed to load torch/ctranslate2).",
                "status": "error"
            })
            await websocket.close(code=4003)
        except Exception:
            pass
        return

    await websocket.accept()
    try:
        audio_buffer = bytearray()
        while True:
            data = await websocket.receive_bytes()
            audio_buffer.extend(data)
            
            # 16kHz, 16-bit mono PCM = 32000 bytes per second.
            # Chunk transcription at 5 seconds of accumulated audio = 160,000 bytes.
            target_bytes = 160000 
            if len(audio_buffer) >= target_bytes:
                chunk_bytes = bytes(audio_buffer[:target_bytes])
                audio_buffer = audio_buffer[target_bytes:]
                
                text = await asyncio.to_thread(streaming_processor.transcribe_pcm, chunk_bytes)
                await websocket.send_json({
                    "text": text,
                    "status": "partial"
                })
    except WebSocketDisconnect:
        pass
    except Exception as e:
        sys.stderr.write(f"[WebSocket Error] Exception: {e}\n")
        try:
            await websocket.close()
        except:
            pass

@app.websocket("/ws/progress")
async def progress_websocket(websocket: WebSocket, token: str):
    if token != args.token:
        await websocket.close(code=4001)
        return
        
    await websocket.accept()
    active_websockets.append(websocket)
    try:
        while True:
            await websocket.receive_text()
    except WebSocketDisconnect:
        if websocket in active_websockets:
            active_websockets.remove(websocket)

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="info")
