import sqlite3
import os
import json
from datetime import datetime

class DbHelper:
    def __init__(self, db_path: str):
        self.db_path = db_path

    def _get_connection(self):
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def get_job_status(self, job_id: str) -> str:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT status FROM job_queue WHERE id = ?", (job_id,))
            row = cursor.fetchone()
            return row["status"] if row else "NOT_FOUND"

    def update_job_status(self, job_id: str, status: str, error_message: str = None):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "UPDATE job_queue SET status = ?, error_message = ?, updated_at = ? WHERE id = ?",
                (status, error_message, datetime.utcnow().isoformat(), job_id)
            )
            conn.commit()

    def get_media_file_id_by_job(self, job_id: str) -> str:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT media_file_id FROM job_queue WHERE id = ?", (job_id,))
            row = cursor.fetchone()
            return row["media_file_id"] if row else None

    def get_project_id_by_job(self, job_id: str) -> str:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT project_id FROM job_queue WHERE id = ?", (job_id,))
            row = cursor.fetchone()
            return row["project_id"] if row else None

    def insert_audio_chunk(self, chunk_id: str, media_file_id: str, chunk_index: int, start_time: float, end_time: float, file_path: str):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT INTO audio_chunks (id, media_file_id, chunk_index, start_time, end_time, file_path, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (chunk_id, media_file_id, chunk_index, start_time, end_time, file_path, "PENDING", datetime.utcnow().isoformat())
            )
            conn.commit()

    def update_audio_chunk_status(self, chunk_id: str, status: str):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "UPDATE audio_chunks SET status = ? WHERE id = ?",
                (status, chunk_id)
            )
            conn.commit()

    def get_completed_chunks(self, media_file_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT chunk_index, file_path, start_time, end_time FROM audio_chunks WHERE media_file_id = ? AND status = 'COMPLETED' ORDER BY chunk_index",
                (media_file_id,)
            )
            return [dict(row) for row in cursor.fetchall()]

    def update_checkpoint(self, job_id: str, stage: str, chunk_offset: int, total_chunks: int, payload: dict):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            checkpoint_id = f"{job_id}_{stage}"
            cursor.execute(
                "INSERT OR REPLACE INTO job_checkpoints (id, job_id, pipeline_stage, chunk_index_offset, total_chunks_count, state_payload, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                (checkpoint_id, job_id, stage, chunk_offset, total_chunks, json.dumps(payload), datetime.utcnow().isoformat())
            )
            conn.commit()

    def get_checkpoint(self, job_id: str, stage: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            checkpoint_id = f"{job_id}_{stage}"
            cursor.execute("SELECT chunk_index_offset, total_chunks_count, state_payload FROM job_checkpoints WHERE id = ?", (checkpoint_id,))
            row = cursor.fetchone()
            if row:
                return {
                    "chunk_offset": row["chunk_index_offset"],
                    "total_chunks": row["total_chunks_count"],
                    "payload": json.loads(row["state_payload"]) if row["state_payload"] else {}
                }
            return None

    def insert_transcript_master(self, project_id: str, media_file_id: str, raw_text: str, segments: list):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            transcript_id = f"t_{media_file_id}"
            
            cursor.execute(
                "INSERT OR REPLACE INTO transcripts (id, project_id, media_file_id, raw_text, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                (transcript_id, project_id, media_file_id, raw_text, datetime.utcnow().isoformat(), datetime.utcnow().isoformat())
            )
            
            cursor.execute("DELETE FROM transcript_words WHERE transcript_id = ?", (transcript_id,))
            
            for seg in segments:
                cursor.execute(
                    "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
                    (transcript_id, seg["text"], seg["start"], seg["end"], seg.get("speaker", "Speaker_00"), seg.get("confidence", 1.0))
                )
            
            conn.commit()

    def get_speaker_profiles(self) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT id, original_id, display_name, voiceprint_embedding FROM speaker_profiles")
            return [dict(row) for row in cursor.fetchall()]

    def insert_speaker_profile(self, profile_id: str, original_id: str, display_name: str, voiceprint_embedding: bytes):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT INTO speaker_profiles (id, original_id, display_name, voiceprint_embedding, created_at) VALUES (?, ?, ?, ?, ?)",
                (profile_id, original_id, display_name, voiceprint_embedding, datetime.utcnow().isoformat())
            )
            conn.commit()

    def get_job_settings(self, job_id: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT selected_glossaries, selected_role, selected_template FROM job_queue WHERE id = ?", (job_id,))
            row = cursor.fetchone()
            if row:
                glossaries_json = row["selected_glossaries"]
                return {
                    "selected_glossaries": json.loads(glossaries_json) if glossaries_json else [],
                    "selected_role": row["selected_role"],
                    "selected_template": row["selected_template"]
                }
            return {
                "selected_glossaries": [],
                "selected_role": None,
                "selected_template": None
            }

    def get_project_segments(self, project_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                """
                SELECT tw.word as text, tw.start_time as start, tw.end_time as end, tw.speaker_id as speaker, t.media_file_id 
                FROM transcripts t 
                JOIN transcript_words tw ON t.id = tw.transcript_id 
                WHERE t.project_id = ?
                """,
                (project_id,)
            )
            return [dict(row) for row in cursor.fetchall()]
