# Changelog

All notable changes to the Momo Transcription Platform will be documented in this file.

## [4.4.0] - 2026-07-02

### Phase 4-4: DOCX Track Changes – Full Release

Complete implementation of Word-compatible Track Changes export, revision status management, real-time SignalR sync, and Accept/Reject UI.

### Added
- **Revisions Status Schema** (`P4-4-1`): `status` column (`pending / accepted / rejected`) with SQLite migrations in C# and Python.
- **Accept / Reject API** (`P4-4-1`): `PATCH /api/v1/revisions/{id}` and `POST /api/v1/transcripts/{id}/revisions/batch`; reject triggers auto-rollback to the last accepted version.
- **OpenXML Track Changes Exporter** (`P4-4-2`): Stream-based `<w:ins>` / `<w:del>` output with author & date metadata; dual-script fonts (DFKai-SB 12 pt / Times New Roman 11 pt), 6-level Chinese outline numbering, A4 page layout (2 cm margins).
- **SignalR Revision-Status Sync** (`P4-4-3`): `UpdateRevisionStatus` hub method broadcasts `revision_status_changed`; client subscribes via `OnRevisionStatusChanged` and auto-reloads version tree.
- **Accept / Reject / Batch UI** (`P4-4-4`): `✓ Accept`, `✗ Reject`, `↩ Rollback` buttons in Diff panel; `✓ Accept All` / `✗ Reject All` batch buttons in version tree footer; `StatusToBrushConverter` renders color-coded badges (pending=blue, accepted=green, rejected=red).

### Tests
- 25 total tests (24 pass / 1 skip); `CollabTrackChangesTests` verifies two-client SignalR broadcast E2E.

## [4.4.0-alpha2] - 2026-07-02

### Phase 4-4: DOCX Track Changes Exporter (Milestone P4-4-2)

This release implements standard layout templates, margins, dual-fonts, 6 outline levels, and native `<w:ins>` / `<w:del>` track changes export using OpenXML stream-based writer.

### Added
- **OpenXML Track Changes Export**: Added stream-based `.docx` exporter mapping pending revisions to native `<w:ins>` / `<w:del>` XML tags with `Author` and `Date` metadata.
- **A4 Layout & Default Spacing**: Standardized page layout with 2cm Left/Right margins (`1134` dxa) and paragraph properties having 1.15 line spacing and 6pt Before/After spacing (`120` dxa).
- **Dual-Script Fonts**: Integrated `styles.xml` doc defaults mapping English/Numbers to `Times New Roman` (11 pt) and Chinese characters to `DFKai-SB` (12 pt).
- **6-Level Outlines**: Integrated `numbering.xml` multi-level lists supporting six levels of outlines (Chinese counting, decimal, letter formats) for heading outlines.
- **Exporter Unit Tests**: Added `DocxTrackChangesTests.cs` verifying XML margins, paragraph properties, and track changes count in-memory.

## [4.4.0-alpha1] - 2026-07-02

### Phase 4-4: DOCX Track Changes (Milestone P4-4-1)

This release implements revisions status schemas, status management REST APIs, and automated test cases.

### Added
- **Revisions Status Schema**: Added `status` column to the `revisions` table (`pending`, `accepted`, `rejected`, defaulting to `pending`) with dynamic check constraint and SQLite migrations in C# and Python.
- **Accept/Reject API Endpoints**: Created `PATCH /api/v1/revisions/{id}` to accept/reject revisions (rejecting triggers rollback to the closest preceding accepted version).
- **Batch Revisions Endpoint**: Created `POST /api/v1/transcripts/{id}/revisions/batch` supporting batch accept/reject updates.
- **Status & Reversion Tests**: Added python unit tests verifying the status update transitions, batch actions, and reject rollback chains.

## [4.3.0] - 2026-07-02

### Phase 4-3: Version Tree & Diff UI

This release introduces the Version History timeline tree panel, dynamic multi-granularity diff comparisons, and real-time collaboration rollback sync.

### Added
- **Version History UI Tab**: Implemented timeline version list search panel, search filters, and show/hide Autosaves checkbox to filter timeline noise.
- **Side-by-Side Diff Panel**: Connected dual-column comparative viewport displaying deleted (red), inserted (green), and modified (high contrast blue `#364F6B`) line segments with Line, Word, and Character granularity controls.
- **Rollback Sync**: Hooked up uvicorn rollback trigger and SignalR broadcast events to clear local OT queue and reload editor contents dynamically.
- **xUnit Concurrency Isolation**: Configured isolated database naming using GUIDs (`momo_ui_version_test_{Guid}.db`) to support clean concurrent parallel test runs in xUnit.
- **Collaboration Fix**: Fixed a critical race condition in `StartCollabConnectionAsync` where `_activeTranscriptId` was cleared due to asynchronous disconnect call.

## [4.0.0-alpha1] - 2026-06-26

### Phase 4: Collaboration & Versioning (Milestone P4-1)

This release kicks off Phase 4, introducing real-time multi-client text editing synchronization and collaborative cursor presence.

### Added
- **SignalR CollabHub Server**: Integrated ASP.NET Core SignalR and built `CollabHub` handling real-time editing rooms, user presence notifications, cursor updates, and broadcast routing.
- **In-process Web Host**: Implemented `CollabServerHost` to host the SignalR Hub inside the C# process using Kestrel on port 5192.
- **Operational Transform (OT) Engine**: Built `OtEngine` supporting concurrency transforms for insert and delete deltas on individual paragraphs to guarantee multi-client document convergence.
- **Real-time Collaboration Client**: Implemented `CollabClient` using ASP.NET Core SignalR Client to sync paragraph edits, caret cursor movements, and presence lists.
- **UI Cursor Presence**: Added visual text highlights/labels in Avalonia `MainWindow` to show the presence and cursor focus of other active editors in the paragraphs panel.
- **OT Concurrency Test**: Added `Test_OT_Sync_TwoClients` integration test validating index shifting and text convergence correctness during concurrent edits.

## [3.0.0] - 2026-06-26

### Phase 3: RAG Orchestrator, Live Streaming, and Advanced UI Controls

This release completes Phase 3 of the Momo Transcription Platform, integrating hybrid vector search, external reference retrieval, real-time audio websocket streaming, and interactive timeline & tag cloud analysis controls.

### Added
- **Qdrant Vector Database Integration**: Added docker-compose settings and implemented Qdrant API client for indexing and vector similarity search.
- **PubMed & SEC EDGAR Providers**: Built provider clients with built-in rate-limiting, retry policies, and custom User-Agents to query PubMed publications and SEC corporate filings.
- **Hybrid Query Router & Prompt Builder**: Implemented reciprocal rank fusion (RRF) to merge BM25 keyword and Qdrant vector results, dynamically constructing contextual prompts with transcriptions and external research findings.
- **WebSocket Streaming Transcription**: Added `/ws/live-stream` endpoint in Python worker for real-time 16-bit 16kHz mono PCM transcription via Whisper tiny-int8 and integrated C# `AudioStreamingService`.
- **Interactive Speaker Timeline**: Created custom Avalonia control `SpeakerTimeline` displaying speaker turn segments as color-coded blocks, supporting click-to-jump playhead snapping.
- **Glossary-driven Tag Cloud**: Implemented TF-IDF tag analyzer scaling tag fonts and colors, highlighting corresponding paragraphs and seeking audio position on click.
- **End-to-End Testing Matrix**: Added `Test_RAG_EndToEnd`, `Test_Streaming_EndToEnd`, and `Test_UI_Flow` tests, implementing headless environment audio device check optimizations and sequential execution controls.

### Fixed
- Fixed unmanaged `AccessViolationException` crashes caused by concurrent NAudio resource disposal on different threads by implementing class-level lock synchronization.
- Fixed `NullReferenceException` crashes in NAudio waveIn stop/dispose by skipping `WaveInEvent` initialization when `WaveIn.DeviceCount == 0` on headless servers.
- Fixed test hang and performance overhead by implementing a short-circuit threshold detector in Python streaming server to immediately return `"silence"` for low-energy audio.

## [2.0.0] - 2026-06-22

### Phase 2: VC Research Copilot

This release introduces the VC Research Copilot capabilities, including dynamic domain glossaries, cross-file database speaker voiceprint memory, Stubble.Core Mustache template compilation, and the right-hand Avalonia settings panel.

### Added
- **Database Schema Upgrades**: Initialized SQLite WAL migrations for the `speaker_profiles` voiceprint table and added job config columns (`selected_glossaries`, `selected_role`, `selected_template`) to `job_queue`.
- **Cos-Similarity Speaker Reconciler**: Implemented Python-side average speaker crop embedding calculation and Cosine similarity threshold comparison (>= 0.80) to reuse Speaker display names across separate files.
- **Glossary Replacement Engine**: Created `GlossaryProcessor` in Python with compiled regex caching, key length descending priority sorting, and case-insensitive word-boundary substitutions.
- **Stubble.Core Template Exporter**: Integrated Stubble Mustache engine to allow rendering custom markdown template layouts into standard C# TXT and DOCX files.
- **Dashboard Sidebar settings UI**: Built a collapsible Avalonia side panel to browse and select glossary files, YAML research roles, and Mustache templates, binding configuration state directly to queue ingestion.
- **Robust Integration Testing**: Added new integration tests validating glossary edge cases, identical speaker database-backed recognition, and Stubble Markdown template renders.

### Fixed
- Fixed Python worker module import errors in integration tests by dynamically resolving and appending absolute workspace paths.
- Fixed subprocess deadlocks in C# tests by writing inline Python commands into temporary script files on disk and redirecting only stdout streams.
- Fixed python indentation syntax errors in `glossary_processor.py`.

## [1.10.0] - 2026-06-18

### Stage 1.10: Quality & Exporter Refinements

This release focuses on transcription segment de-duplication, overlap chunk stitching, performance optimizations, upgraded structured exports, and robust CI test controls.

### Added
- **[SkippableFact] Test Controller**: Dynamic test attribute in C# to skip CPU-intensive 1.8-hour audio tests in fast CI runs while allowing nightly full execution via environment variables.
- **Dynamic Ending Overlap**: Added logic to ffmpeg audio splitting to step audio with 2-second overlap, ensuring the last chunk ends exactly at the audio duration without padding extra silence.
- **Speaker Tags in Subtitles**: Automatically prepended speak identifiers `Speaker_X：` to SRT subtitle rows.
- **Word Document Headings Outline**: Enabled Outline Level 1 hierarchy in Word styles on Speaker paragraphs to facilitate sidebar outline navigation.

### Changed
- **De-duplication Generator**: Rewrote segment de-duplication comparison logic in python as a generator (`merge_segments`) to reduce peak memory usage during large audio processing.
- **Text Normalization**: Standardized string normalizations via NFC/NFKC Unicode schemas to properly align full/half-width Chinese/English characters and punctuation marks before duplicate checks.
- **Sentence Bounds**: Adjusted WhisperX word-level sentence group limits to break paragraphs at sentence delimiters, speaker transitions, or upon hitting 6-second or 120-character caps.
- **StringBuilder Optimizations**: Restructured C# `ParagraphBuilder` to process word collections using StringBuilder, minimizing memory fragmentation.
- **TXT Format & Encoding**: Updated TXT exporter layout to `[hh:mm:ss] Speaker_X：text` and explicitly configured output in **UTF-8 with BOM** to prevent Chinese character corruption on Windows.
- **Test Assertion Sequence**: Sequenced integration test validations to evaluate TXT format regex, DOCX OpenXML styles, and SRT subtitle counts consecutively, ensuring failures are easily trackable.

### Fixed
- Fixed fragile SRT first/last text assertions that would fail if standard Whisper filler words (like "You") appeared at boundaries.
- Fixed apad infinite loop issues by explicitly specifying segment durations to ffmpeg slice commands.
