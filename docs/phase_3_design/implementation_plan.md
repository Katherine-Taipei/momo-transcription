# Phase 3 Implementation Plan: Multimodal RAG, Live Streaming & Timeline UI

This document specifies the module breakdown, task list, external dependencies, test matrix, and finalized architectural decisions for the Phase 3 implementation.

---

## 1. Module Decomposition & Task List

We break down the work into 6 sequential milestones (P3-1 to P3-6) matching the release checklist.

### P3-1: Qdrant Docker PoC & Embedding Ingest
*   [ ] Configure local Qdrant container compose or file-backed database bootstrap.
*   [ ] Define schema mapping for Qdrant collection (payload fields: `transcript_id`, `speaker_id`, `start_time`, `end_time`, `text`, `project_id`).
*   [ ] Implement Python worker utility using `qdrant-client` to create collections and upsert text chunks with embeddings.

### P3-2: PubMed & SEC EDGAR Provider SDK Wrappers
*   [ ] Create standard HTTP client wrapper for PubMed Entrez Utilities API (Search and Fetch tools).
*   [ ] Create SEC EDGAR HTTP client wrapper adhering strictly to the SEC User-Agent header requirements.
*   [ ] Implement rate limiting and automatic retries for both providers.

### P3-3: RAG Orchestrator (Hybrid Search & Prompt Builder)
*   [ ] Implement hybrid query router in Python (extracts search keywords from glossary matching, runs keyword BM25 query + Qdrant vector query, and merges results).
*   [ ] Construct prompt builder compiling the relevant transcripts context and external findings into a target LLM prompt.
*   [ ] Integrate inference provider client (local Ollama endpoint or external model API) to generate final research report.

### P3-4: WebSocket Streaming PoC
*   [ ] Implement C# Avalonia streaming recorder buffer slicing audio every 5 seconds.
*   [ ] Create WebSocket listener endpoint `/ws/live-stream` on the Python worker.
*   [ ] Implement streaming transcription loop executing a local Whisper `tiny` model in `int8` on raw incoming PCM chunks.

### P3-5: Speaker Timeline & Tag Cloud UI
*   [ ] Build Speaker Timeline visual component in Avalonia displaying chronologically stacked, colored rectangles mapping speaker durations.
*   [ ] Implement click-to-jump timeline playhead binding.
*   [ ] Build Keyword Tag Cloud displaying density-scaled terms matching the domain glossary.

### P3-6: Test Matrix Consolidation & Release v3.0.0
*   [ ] Add Python and C# integration tests validating Qdrant, PubMed, SEC EDGAR, and Streaming.
*   [ ] Package all updates and compile Release v3.0.0 tag.

---

## 2. External Dependencies

To run the RAG and search capabilities locally, developers will require:

1.  **Qdrant Vector Database**:
    *   Docker Image: `qdrant/qdrant:latest`
    *   Ports: `6333` (REST HTTP API), `6334` (gRPC API)
    *   Volume Mounts: Local directories for persistent collection storage.
2.  **External APIs & Access Keys**:
    *   **PubMed (NCBI E-Utilities)**: No key required for basic rate limit (3 requests/sec). An NCBI API key configured via environment variable `PUBMED_API_KEY` raises the limit to 10 requests/sec.
    *   **SEC EDGAR**: No API key required, but requires compliant User-Agent headers declaring organization name and email (configured via `appsettings.json` as `MomoResearchBot/1.0 (<email>)`).
3.  **Local LLM Host (Ollama)**:
    *   Requires local Ollama service running on `http://127.0.0.1:11434` with `llama3` or `qwen2.5` model pre-downloaded.

---

## 3. Test Matrix

### Unit Tests
*   **PubMed Client**: Mock NCBI REST responses and verify ESearch/EFetch XML parsing boundaries.
*   **SEC EDGAR Client**: Mock SEC JSON responses and verify user-agent header checks.
*   **Embeddings & Normalization**: Verify text chunking yields consistent vectors.

### Integration Tests
*   **Qdrant integration**: Connect to local Qdrant container, insert mock embeddings, and run cosine similarity search.
*   **WebSocket stream flow**: Simulate C# WebSocket client pushing PCM data chunks to `/ws/live-stream` and verify FastAPI returns transcribed segment JSON objects.

### End-to-End (E2E) Tests
*   **Dashboard RAG View**: Verify query submission returns LLM research summaries referencing transcripts.
*   **Timeline Timeline Click**: Verify playhead skips to selected speaker blocks.

---

## 4. Finalized Architectural Decisions

The following architectural choices have been approved for Phase 3:

> [!NOTE]
> **Q1: RAG Orchestrator Hosting Location**
> *   **Selected**: **Option A (Python Worker side)**. Hybrid search and orchestrations reside in Python. C# invokes FastAPI endpoints. Keeps ML and search package footprints off the C# application build.

> [!NOTE]
> **Q2: Embedding Generation Model**
> *   **Selected**: **Option A (Local sentence-transformers)**. The local model `all-MiniLM-L6-v2` runs offline, avoiding external API dependencies or external Ollama service calls for simple ingestion.

> [!NOTE]
> **Q3: WebSocket Streaming Transcription Model**
> *   **Selected**: **Option A (Local Whisper tiny int8)**. Employs a local Whisper model on CPU threads, giving extremely low latency (< 900 ms for 5s buffers) fully offline.

> [!NOTE]
> **Q4: Provider Keys & Headers Configuration**
> *   **PubMed**: Defaults to anonymous access. Environment variable `PUBMED_API_KEY` is loaded if present to raise rate limits.
> *   **SEC EDGAR**: Headers use compliant user-agent structure (`MomoResearchBot/1.0 (contact@company.com)`) configured via `appsettings.json`.
