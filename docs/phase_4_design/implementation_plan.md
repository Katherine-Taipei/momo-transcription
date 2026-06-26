# Phase 4 Implementation Plan: Collaboration & Versioning (Draft)

This document provides details on database schemas, implementation milestones, and core decisions for **Phase 4: Collaboration & Versioning**.

---

## 1. Development Milestones

| Milestone | Target Contents & Activities | Key Outputs |
| :--- | :--- | :--- |
| **P4-1** | SignalR CollabHub + OT Engine PoC | Dual-client text synchronization and cursor tracking. |
| **P4-2** | Comment & Task API + UI Panel | Threaded comments, state tracking, and `@mention` routing. |
| **P4-3** | Revision DB Schema & Diff Viewer | Double-column visual difference comparison and rollback engine. |
| **P4-4** | DOCX Track Changes Export | OpenXML-native Track Changes tags (`w:ins` / `w:del`). |
| **P4-5** | E2E Testing Matrix & CI Integration | Simulated multi-client concurrency tests. |

---

## 2. Database Schema Extensions

We will extend the SQLite schema with the following tables:

```sql
CREATE TABLE revisions (
  id TEXT PRIMARY KEY,
  transcript_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  created_by TEXT NOT NULL,
  delta_json TEXT NOT NULL,
  FOREIGN KEY(transcript_id) REFERENCES transcripts(id)
);

CREATE TABLE comments (
  id TEXT PRIMARY KEY,
  transcript_id TEXT NOT NULL,
  range_start REAL NOT NULL,
  range_end REAL NOT NULL,
  author TEXT NOT NULL,
  status TEXT NOT NULL,          -- 'open' / 'resolved'
  text TEXT NOT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY(transcript_id) REFERENCES transcripts(id)
);
```

---

## 3. Key Technical Decisions

| ID | Topic | Recommendation | Rationale |
| :--- | :--- | :--- | :--- |
| **Q1** | **OT or CRDT?** | **OT (Operational Transform)** | Simpler synchronization logic and lower processing cost for text-heavy documents. |
| **Q2** | **Sync Protocol** | **SignalR + MsgPack** | Native integration on the C# Avalonia client side, fast serialization, and robust binary transport. |
| **Q3** | **Diff Algorithm** | **DiffPlex NuGet** | Feature-rich C# diff utility for side-by-side output comparison; fallback to Python's `difflib` wrapper on daemon. |
