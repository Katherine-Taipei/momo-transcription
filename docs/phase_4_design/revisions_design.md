# Phase 4-3: Revisions & Diff Design (Finalized)

This document outlines the finalized system design and implementation plan for Milestone P4-3 (Version History, Diff Comparison, and Rollback).

---

## 1. Database Schema

### `revisions` Table
We will store complete snapshots of the transcript text for simplicity, reliability, and immediate rollback execution.
```sql
CREATE TABLE IF NOT EXISTS revisions (
    id TEXT PRIMARY KEY,
    transcript_id TEXT NOT NULL,
    version_number INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NOT NULL,
    description TEXT,
    snapshot_text TEXT NOT NULL,
    FOREIGN KEY(transcript_id) REFERENCES transcripts(id) ON DELETE CASCADE
);
```

---

## 2. API Endpoints

### 2.1 Create Revision
* **Endpoint:** `POST /api/v1/revisions`
* **Request Body:**
  ```json
  {
    "transcript_id": "string",
    "created_by": "string",
    "description": "string"
  }
  ```
* **Behavior:**
  * The backend reads the current state of the transcript directly from the database and saves it as a new revision snapshot.
  * **Timing & Triggers:**
    1. **Manual Save:** Triggered by user request (explicit description provided).
    2. **Autosave:** Automatically triggered by the system every 2 minutes or after 2,000 characters of modifications.

### 2.2 Get Revision List
* **Endpoint:** `GET /api/v1/transcripts/{transcript_id}/revisions`
* **Response:** Returns list of revisions sorted by version number in descending order.

### 2.3 Get Revision Detail
* **Endpoint:** `GET /api/v1/revisions/{id}`
* **Response:** Returns detailed revision metadata and the full `snapshot_text`.

### 2.4 Rollback to Revision
* **Endpoint:** `POST /api/v1/revisions/{id}/rollback`
* **Behavior:**
  1. Overwrites the active `transcripts` text with the revision's `snapshot_text`.
  2. Automatically creates a new revision entry with description `AUTO_ROLLBACK` (recording the state prior to rollback).
  3. Broadcasts a SignalR event `rollback_applied` to all connected collaboration clients, forcing the editor to reload and sync the rolled-back content.

---

## 3. Diff Comparison Engine (DiffPlex Client-Side)

* **Location:** Client-side (C# Avalonia app).
* **API Details:** The API only returns the raw text versions.
* **Diff Engine:** The Avalonia application will utilize the `DiffPlex` NuGet package directly to perform side-by-side (two-column) comparisons in the UI.

---

## 4. UI Layout (Avalonia Version Tree & Diff Panel)

* **Version Tree (Left Panel):** A vertical timeline list showing the history of versions (timestamps, authors, descriptions, version numbers).
* **Diff Panel (Right Panel):** A side-by-side, read-only two-column comparison highlighting additions (green) and deletions (red).
* **Granularity Toggle:** Controls to switch diff highlights between character-level, word-level, and line-level comparisons.

---

## 5. Development Schedule

| Phase | Milestone | Focus Area | ETA |
| :--- | :--- | :--- | :--- |
| **P4-3-1** | DB & API | SQLite revisions table, POST/GET endpoints, creation/list/detail APIs | +6 WD |
| **P4-3-2** | Rollback & Sync | Database rollback mechanism, automatic revision checkpointing, SignalR `rollback_applied` broadcast | +10 WD |
| **P4-3-3** | UI Integration | Avalonia left timeline tree, DiffPlex two-column read-only panel, granularity toggle | +14 WD |
