# Phase 3 Task List: Multimodal RAG, Live Streaming & Timeline UI

This checklist tracks Phase 3 development items. Mark items as completed as progress is made.

---

## P3-1: Qdrant Docker PoC & Embedding Ingest
- [x] Configure local Qdrant container compose or file-backed database bootstrap
- [x] Define schema mapping for Qdrant collection (payload fields: `transcript_id`, `speaker_id`, `start_time`, `end_time`, `text`, `project_id`)
- [x] Implement Python worker utility using `qdrant-client` to create collections and upsert text chunks with embeddings

## P3-2: PubMed & SEC EDGAR Provider SDK Wrappers
- [x] Create standard HTTP client wrapper for PubMed Entrez Utilities API (Search and Fetch tools)
- [x] Create SEC EDGAR HTTP client wrapper with compliant User-Agent headers
- [x] Implement rate limiting and automatic retries for both providers

## P3-3: RAG Orchestrator (Hybrid Search & Prompt Builder)
- [x] Implement hybrid query router in Python (extracts search keywords from glossary matching, runs keyword BM25 query + Qdrant vector query, and merges results)
- [x] Construct prompt builder compiling transcripts context and external findings into a target LLM prompt
- [x] Integrate inference provider client (local Ollama endpoint or external model API) to generate final research report

## P3-4: WebSocket Streaming PoC
- [x] Implement C# Avalonia streaming recorder buffer slicing audio every 5 seconds
- [x] Create WebSocket listener endpoint `/ws/live-stream` on the Python worker
- [x] Implement streaming transcription loop executing a local Whisper `tiny` model in `int8` on raw incoming PCM chunks

## P3-5: Speaker Timeline & Tag Cloud UI
- [x] Build Speaker Timeline visual component in Avalonia displaying chronologically stacked, colored rectangles mapping speaker durations
- [x] Implement click-to-jump timeline playhead binding
- [x] Build Keyword Tag Cloud displaying density-scaled terms matching the domain glossary

## P3-6: Test Matrix Consolidation & Release v3.0.0
- [ ] Add Python and C# integration tests validating Qdrant, PubMed, SEC EDGAR, and Streaming
- [ ] Package all updates and compile Release v3.0.0 tag
