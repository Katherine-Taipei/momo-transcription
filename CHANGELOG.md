# Changelog

All notable changes to the Momo Transcription Platform will be documented in this file.

## [5.0.0-beta1] - 2026-07-06

### Phase 5-4: Visual Polish & Documentation (Feature Freeze)

This release introduces rich rendering for update changelogs via a custom Markdown-to-UI engine, layout enhancements, responsive dark-theme hyperlinks, automatic image caching, and comprehensive user guides.

### Added
- **Rich Markdown Traversal Engine** (`P5-4-3a`): Implemented a custom Markdig AST renderer converting headers, lists, and code blocks directly into styled native Avalonia controls inside the update available dialog.
- **Sanitized Hyperlinks & Contrast Styles** (`P5-4-3a`): Enforced HTTP/HTTPS scheme whitelisting for update hyperlinks (blocking `javascript:` actions) and styled links with higher contrast hover colors (`MarkdownHyperlinkStyle`).
- **Asynchronous Image Downloader & Cache** (`P5-4-3a`): Added an asynchronous thread downloader that caches remote changelog images under `%LOCALAPPDATA%\Momo\cache\img`, automatically constrains image MaxWidth=480, and falls back to text placeholders on a 10s download timeout.
- **Installation Progress & Locking** (`P5-4-3b`): Configured `IsInstallingUpdate` locking flags in `MainViewModel` to disable interactive buttons and show an indeterminate loading progress spinner during updates execution.
- **Safety Truncation** (`P5-4-3a`): Added a 100 kB safety buffer limit on Github release body content inside `UpdateManager` to safeguard against memory/buffer exhaustion.
- **Enhanced Test Suites** (`P5-4-3d`): Implemented strict placeholder format/count parity tests across zh-TW and en-US localization resources, and mock HTTP handler tests checking asynchronous image downloads and local caching.
- **User Documentation & Media Assets** (`P5-4-4`): Generated screen captures for settings sidebars and GDPR modals, created user-facing `README.md` and `wiki_sidebar.md` guides inside the `docs/` repository.

## [5.0.0-alpha5] - 2026-07-05

### Phase 5-4: UI & UX Alignment - Milestone Release

This release implements localized zh-TW / en-US translation resource files, dynamic theme and language switching controls, custom UpdateFrequency settings, persistent update prompt suppression, GDPR/Telemetry Opt-in modals, and background update schedule execution.

### Added
- **Dynamic Localization & Culture Sync** (`P5-4-2a`): Added `Strings.zh-TW.json` and `Strings.en-US.json` localized dictionaries, supporting on-the-fly language switching and automatic synchronization of thread `CultureInfo` and formatting.
- **Settings Controls & Persistent Storage** (`P5-4-2b`): Added language dropdown, update channel, and enum-backed `UpdateFrequency` (`Off=0`, `Daily=1`, `Weekly=2`) dropdown options to the settings sidebar of `MainWindow.axaml`. Saved selections persistently inside LocalAppData settings.
- **GDPR / Telemetry Opt-in Popups** (`P5-4-2b`): Implemented a GDPR disclosures overlay modal showing on first launch, with prompt count tracking and 7-day interval re-prompt constraints if deferred.
- **Update Modal & Markdig Changelog** (`P5-4-2c`): Configured `Markdig` to parse release notes markdown into clean text inside the Update Available dialog overlay.
- **Background Update Scheduler** (`P5-4-2d`): Implemented `UpdateBackgroundWorker` running tasks in the background periodically to check updates based on interval schedules and suppressions.
- **UI and Localization Unit Tests** (`P5-4-2d`): Added xUnit test suites verifying dynamic translation key matches, telemetry suppression boundaries, and scheduler calculations.

## [5.0.0-alpha4] - 2026-07-05

### Phase 5-4: Secure Updater & Rollback - Core Release

This release implements secure signature pinning verification, automatic backup/restore rollback logic, HTTP 429 backoff rate limiting, and log rotation for the update service.

### Added
- **SHA-256 Pinning & WinVerifyTrust** (`P5-4-1a`): Integrated Authenticode verification via `WinVerifyTrust` and certificate public key SHA-256 fingerprint pinning loaded from `public_keys.json` with memory caching.
- **Atomic Zip Backup & Auto-Rollback** (`P5-4-1b`): Implemented recursive zip backup of binary directories with retention of 2 backups. Added startup failure count tracking in `AppSettings` triggering automatic backup restoration. Locked file conflicts during rollback are handled by renaming files on-the-fly.
- **Rollback Failure Safety** (`P5-4-1b`): Configured `!ROLLBACK-FAILED` flag file to handle restoration failure, prompting manual installation on next boot.
- **HTTP 429 Retry & Suppression** (`P5-4-1c`): Added 5 retries with exponential backoff on HTTP 429, suppressing update checks for 24 hours on consecutive failure.
- **Daily Log Rotation** (`P5-4-1c`): Implemented daily rotating `update.log` with automatic purging of logs older than 30 days.
- **E2E Validation and Tests** (`P5-4-1d`): Extended update and telemetry test suite verifying certificate pinning, malicious file rejection/deletion, rate-limiting retry, and locked-file rollback recovery.

## [5.0.0-alpha3] - 2026-07-05

### Phase 5-3: UI Minor Changes & Installer Packaging

This release implements dynamic light/dark theme switching, persistent appsettings.json storage in LocalAppData, corporate logo headers, momoctl CLI script wrappers, and automated Inno Setup prerequisites check.

### Added
- **Dynamic Theme Switcher** (`P5-3-1`): Configured dynamic FluentTheme variant selection in App.axaml and added Theme selection ComboBox to the sidebar footer (supporting Auto, Light, Dark options).
- **Persistent App Settings** (`P5-3-1`): Implemented `AppSettingsManager` resolving and storing `appsettings.json` under `%APPDATA%\Momo\appsettings.json` to ensure write access under restricted environments, with fallback to installation directory templates.
- **Corporate Logo Header** (`P5-3-1`): Integrated corporate branding logo layouts dynamically resolved from user-defined paths or assets.
- **momoctl CLI & Script Wrapper** (`P5-3-2`): Added python-based `momoctl.py` CLI utility and Windows `.cmd` launchers in workspace root supporting `--version`, `rebuild-index`, and `export-docx` subcommands.
- **Inno Setup Installer** (`P5-3-3`): Configured `momo_setup.iss` installation compiler script with Pascal routines executing `dotnet --list-runtimes` to automatically detect `.NET 8 Desktop Runtime` prerequisites.
- **Unit and Integration Tests** (`P5-3-4`): Extended python test client verifying argparse execution, help listings, version output, and SQLite indexes.

## [5.0.0-alpha2] - 2026-07-03

### Phase 5-2: RAG Vector Cache & Hybrid Search

This release implements SQLite-based dense embedding caching, cross-project global vector collection querying and scoping control, and environment-configurable Reciprocal Rank Fusion (RRF) hybrid scoring.

### Added
- **Embedding Cache Storage** (`P5-2-1`): Added `embedding_cache` database table mapping normalized segment SHA256 `text_hash` to serialized floats JSON representation. Added `created_at` database index for cache expiration cleanups. Implemented cache lookup/write hooks in `rag_orchestrator` yielding a 100% latency speedup (from 5s down to 0s) for cache hits.
- **Global Collection Scoping** (`P5-2-2`): Transitioned to unified global vector index `momo_transcripts` using Qdrant. Embedded `project_id` payload filters to support both local (scoped project-specific) and global (cross-project cross-media) search routines.
- **Hybrid RRF Sorting & Custom K** (`P5-2-3`): Implemented parallel BM25 keyword search and dense Qdrant queries merged via Reciprocal Rank Fusion. Read custom `RRF_K` parameter from environment variables to enable tuning of retrieval gradients.
- **Unit and Stress Tests** (`P5-2-4`): Added python test suite (`test_phase5_p5_2.py`) validating embedding cache hit performance, global vs local filters, RRF k overrides, and 1,000-query RRF sorting stress test under 0.15s (well below 1s limit).

## [5.0.0-alpha1] - 2026-07-03

### Phase 5-1: Transcript Index Rebuild & Search

This release implements fast SQLite index rebuilding, text tokenization, full-text search APIs, and right-sidebar search UI integration.

### Added
- **Fast SQLite Index Rebuild** (`P5-1-1`): Added tokenization for CJK characters and English alphanumeric blocks, realigning word speakers and timestamps using sliding-window algorithm under 18ms for 10k words. Optimized writes via SQLite WAL mode, synchronous=NORMAL, and 1000-row batching. Auto-triggered on database revisions rollback/rejection.
- **REST Search API** (`P5-1-2`): Added `POST /api/v1/transcripts/{id}/rebuild_index` and `GET /api/v1/transcripts/{id}/search?q=query&limit=50` returning paragraph index, 30-char context window, and exact start/end match offsets.
- **Search UI Integration** (`P5-1-3`): Implemented split-panel right sidebar containing search fields and glossary tag clouds in Avalonia. Added `Ctrl+F` shortcut focus, text selection, automatic 3-second highlight fadeout, and multi-occurrence arrow controls.
- **Unit and Integration Tests** (`P5-1-4`): Added python unit test suite (`test_phase5_p5_1.py`) with 10,000-character stress test under 5s, and C# VM integration tests (`SearchTests.cs`) for query and navigation commands.

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
