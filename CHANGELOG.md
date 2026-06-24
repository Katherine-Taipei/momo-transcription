# Changelog

All notable changes to the Momo Transcription Platform will be documented in this file.

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
