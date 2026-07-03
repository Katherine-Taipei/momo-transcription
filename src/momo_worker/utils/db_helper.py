import sqlite3
import os
import json
from datetime import datetime

class DbHelper:
    def __init__(self, db_path: str):
        self.db_path = db_path
        self._initialize_db()

    def _initialize_db(self):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("PRAGMA journal_mode = WAL;")
            cursor.execute("PRAGMA foreign_keys = ON;")
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS comments (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    paragraph_id TEXT NOT NULL,
                    author TEXT NOT NULL,
                    text TEXT NOT NULL,
                    parent_id TEXT,
                    status TEXT NOT NULL DEFAULT 'open',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE,
                    FOREIGN KEY(parent_id) REFERENCES comments(id) ON DELETE CASCADE
                );
            """)
            cursor.execute("CREATE INDEX IF NOT EXISTS idx_comments_paragraph_created ON comments(paragraph_id, created_at);")
            
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    paragraph_id TEXT NOT NULL,
                    assignee TEXT NOT NULL,
                    author TEXT NOT NULL,
                    text TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'open',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
                );
            """)
            cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_paragraph_created ON tasks(paragraph_id, created_at);")
            
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS revisions (
                    id TEXT PRIMARY KEY,
                    transcript_id TEXT NOT NULL,
                    version_number INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    description TEXT,
                    snapshot_text TEXT NOT NULL,
                    status TEXT CHECK(status IN ('pending','accepted','rejected')) DEFAULT 'pending',
                    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE,
                    UNIQUE(transcript_id, version_number)
                );
            """)
            cursor.execute("CREATE INDEX IF NOT EXISTS idx_revisions_transcript_version ON revisions(transcript_id, version_number);")
            
            cursor.execute("PRAGMA table_info(revisions);")
            columns = [row[1] for row in cursor.fetchall()]
            if columns and "status" not in columns:
                cursor.execute("ALTER TABLE revisions ADD COLUMN status TEXT CHECK(status IN ('pending','accepted','rejected')) DEFAULT 'pending';")
            
            conn.commit()

    def _get_connection(self):
        use_uri = self.db_path.startswith("file:") or "?" in self.db_path
        conn = sqlite3.connect(self.db_path, uri=use_uri)
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

    def insert_comment(self, comment_id: str, transcript_id: str, paragraph_id: str, author: str, text: str, parent_id: str = None, status: str = "open", created_at: str = None, updated_at: str = None) -> dict:
        now = datetime.utcnow().isoformat()
        c_at = created_at or now
        u_at = updated_at or now
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                """
                INSERT INTO comments (id, transcript_id, paragraph_id, author, text, parent_id, status, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (comment_id, transcript_id, paragraph_id, author, text, parent_id, status, c_at, u_at)
            )
            conn.commit()
        return {
            "id": comment_id,
            "transcript_id": transcript_id,
            "paragraph_id": paragraph_id,
            "author": author,
            "text": text,
            "parent_id": parent_id,
            "status": status,
            "created_at": c_at,
            "updated_at": u_at
        }

    def update_comment(self, comment_id: str, text: str = None, status: str = None) -> dict:
        now = datetime.utcnow().isoformat()
        with self._get_connection() as conn:
            cursor = conn.cursor()
            if text is not None and status is not None:
                cursor.execute(
                    "UPDATE comments SET text = ?, status = ?, updated_at = ? WHERE id = ?",
                    (text, status, now, comment_id)
                )
            elif text is not None:
                cursor.execute(
                    "UPDATE comments SET text = ?, updated_at = ? WHERE id = ?",
                    (text, now, comment_id)
                )
            elif status is not None:
                cursor.execute(
                    "UPDATE comments SET status = ?, updated_at = ? WHERE id = ?",
                    (status, now, comment_id)
                )
            conn.commit()
        return self.get_comment(comment_id)

    def get_comment(self, comment_id: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM comments WHERE id = ?", (comment_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def get_comments_by_transcript(self, transcript_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM comments WHERE transcript_id = ? ORDER BY created_at ASC", (transcript_id,))
            return [dict(row) for row in cursor.fetchall()]

    def insert_task(self, task_id: str, transcript_id: str, paragraph_id: str, assignee: str, author: str, text: str, status: str = "open", created_at: str = None, updated_at: str = None) -> dict:
        now = datetime.utcnow().isoformat()
        c_at = created_at or now
        u_at = updated_at or now
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                """
                INSERT INTO tasks (id, transcript_id, paragraph_id, assignee, author, text, status, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (task_id, transcript_id, paragraph_id, assignee, author, text, status, c_at, u_at)
            )
            conn.commit()
        return {
            "id": task_id,
            "transcript_id": transcript_id,
            "paragraph_id": paragraph_id,
            "assignee": assignee,
            "author": author,
            "text": text,
            "status": status,
            "created_at": c_at,
            "updated_at": u_at
        }

    def get_task(self, task_id: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM tasks WHERE id = ?", (task_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def update_task_status(self, task_id: str, status: str) -> dict:
        now = datetime.utcnow().isoformat()
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "UPDATE tasks SET status = ?, updated_at = ? WHERE id = ?",
                (status, now, task_id)
            )
            conn.commit()
        return self.get_task(task_id)

    def get_tasks_by_transcript(self, transcript_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM tasks WHERE transcript_id = ? ORDER BY created_at ASC", (transcript_id,))
            return [dict(row) for row in cursor.fetchall()]

    def get_transcript(self, transcript_id: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM transcripts WHERE id = ?", (transcript_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def insert_revision(self, revision_id: str, transcript_id: str, version_number: int, created_by: str, snapshot_text: str, description: str = None, created_at: str = None, status: str = "pending") -> dict:
        now = datetime.utcnow().isoformat()
        c_at = created_at or now
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                """
                INSERT INTO revisions (id, transcript_id, version_number, created_by, snapshot_text, description, created_at, status)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (revision_id, transcript_id, version_number, created_by, snapshot_text, description, c_at, status)
            )
            conn.commit()
        return {
            "id": revision_id,
            "transcript_id": transcript_id,
            "version_number": version_number,
            "created_by": created_by,
            "snapshot_text": snapshot_text,
            "description": description,
            "created_at": c_at,
            "status": status
        }

    def get_revision(self, revision_id: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM revisions WHERE id = ?", (revision_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def get_revisions_by_transcript(self, transcript_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM revisions WHERE transcript_id = ? ORDER BY version_number DESC", (transcript_id,))
            return [dict(row) for row in cursor.fetchall()]

    def get_next_version_number(self, transcript_id: str) -> int:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT MAX(version_number) as max_v FROM revisions WHERE transcript_id = ?", (transcript_id,))
            row = cursor.fetchone()
            max_v = row["max_v"]
            return (max_v + 1) if max_v is not None else 1

    def rollback_transcript(self, transcript_id: str, snapshot_text: str):
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "UPDATE transcripts SET raw_text = ?, updated_at = ? WHERE id = ?",
                (snapshot_text, datetime.utcnow().isoformat(), transcript_id)
            )
            conn.commit()
        self.rebuild_transcript_index(transcript_id)

    def update_revision_status(self, revision_id: str, status: str) -> dict:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "UPDATE revisions SET status = ? WHERE id = ?",
                (status, revision_id)
            )
            conn.commit()
        return self.get_revision(revision_id)

    def get_previous_revision_snapshot(self, transcript_id: str, version_number: int) -> str:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            # 1. Try to find the closest preceding accepted version
            cursor.execute(
                "SELECT snapshot_text FROM revisions WHERE transcript_id = ? AND version_number < ? AND (status = 'accepted' OR status IS NULL) ORDER BY version_number DESC LIMIT 1",
                (transcript_id, version_number)
            )
            row = cursor.fetchone()
            if row:
                return row["snapshot_text"]
            
            # 2. Fallback to any preceding version if no accepted one is found
            cursor.execute(
                "SELECT snapshot_text FROM revisions WHERE transcript_id = ? AND version_number < ? ORDER BY version_number DESC LIMIT 1",
                (transcript_id, version_number)
            )
            row = cursor.fetchone()
            if row:
                return row["snapshot_text"]
                
            return None

    def tokenize_text(self, text: str) -> list:
        if not text:
            return []
        import re
        # Match CJK characters, alphanumeric words, and non-whitespace symbols/punctuation individually
        pattern = re.compile(r'[\u4e00-\u9fff]|\w+|[^\s\w\u4e00-\u9fff]')
        return pattern.findall(text)

    def get_transcript_words(self, transcript_id: str) -> list:
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT word, start_time, end_time, speaker_id, confidence FROM transcript_words WHERE transcript_id = ? ORDER BY start_time",
                (transcript_id,)
            )
            return [dict(row) for row in cursor.fetchall()]

    def rebuild_transcript_index(self, transcript_id: str) -> tuple:
        import time
        start_time_ms = time.time() * 1000

        # 1. Fetch raw_text
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT raw_text FROM transcripts WHERE id = ?", (transcript_id,))
            row = cursor.fetchone()
            if not row:
                return (0, 0.0)
            raw_text = row["raw_text"] or ""

            # 2. Fetch existing words for alignment
            cursor.execute(
                "SELECT word, start_time, end_time, speaker_id, confidence FROM transcript_words WHERE transcript_id = ? ORDER BY start_time",
                (transcript_id,)
            )
            old_words = [dict(r) for r in cursor.fetchall()]

        # Perform tokenization and alignment
        old_tokens = []
        for ow in old_words:
            tokens = self.tokenize_text(ow["word"])
            if not tokens:
                continue
            n = len(tokens)
            duration = ow["end_time"] - ow["start_time"]
            token_dur = duration / n if n > 0 else 0.0
            for idx, t in enumerate(tokens):
                old_tokens.append({
                    "word": t,
                    "start_time": ow["start_time"] + idx * token_dur,
                    "end_time": ow["start_time"] + (idx + 1) * token_dur,
                    "speaker_id": ow["speaker_id"],
                    "confidence": ow["confidence"]
                })

        new_tokens_strings = self.tokenize_text(raw_text)
        new_words = []
        old_idx = 0
        last_end_time = 0.0
        last_speaker = "Speaker_00"

        for nt in new_tokens_strings:
            matched_idx = -1
            # Look ahead up to 50 tokens
            for w_offset in range(50):
                check_idx = old_idx + w_offset
                if check_idx >= len(old_tokens):
                    break
                if old_tokens[check_idx]["word"].lower() == nt.lower():
                    matched_idx = check_idx
                    break
            
            if matched_idx != -1:
                token_info = old_tokens[matched_idx]
                start_time = token_info["start_time"]
                end_time = token_info["end_time"]
                speaker_id = token_info["speaker_id"]
                confidence = token_info["confidence"]
                
                if start_time < last_end_time:
                    start_time = last_end_time
                    end_time = max(start_time + 0.1, end_time)
                
                old_idx = matched_idx + 1
            else:
                # Interpolate new/edited word
                start_time = last_end_time
                end_time = start_time + 0.2
                speaker_id = last_speaker
                confidence = 1.0
                
            last_end_time = end_time
            last_speaker = speaker_id
            
            new_words.append((
                transcript_id,
                nt,
                start_time,
                end_time,
                speaker_id,
                confidence
            ))

        # 3. Write in batches using transaction and WAL options
        with self._get_connection() as conn:
            cursor = conn.cursor()
            # Apply performance optimizations for write speed
            cursor.execute("PRAGMA journal_mode=WAL;")
            cursor.execute("PRAGMA synchronous=NORMAL;")
            
            cursor.execute("DELETE FROM transcript_words WHERE transcript_id = ?", (transcript_id,))
            
            batch_size = 1000
            for i in range(0, len(new_words), batch_size):
                batch = new_words[i:i+batch_size]
                cursor.executemany(
                    "INSERT INTO transcript_words (transcript_id, word, start_time, end_time, speaker_id, confidence) VALUES (?, ?, ?, ?, ?, ?)",
                    batch
                )
            conn.commit()

        # 4. Run VACUUM outside transaction
        try:
            with self._get_connection() as conn:
                conn.isolation_level = None  # needed for vacuum
                conn.cursor().execute("VACUUM")
        except Exception:
            pass

        duration_ms = (time.time() * 1000) - start_time_ms
        return (len(new_words), duration_ms)
