# Phase 2 Implementation Plan: VC Research Copilot

This plan details the code execution steps, project structure modifications, configuration additions, and unit/integration testing strategies to deliver Phase 2.

---

## 1. Specifications & Design Decisions

*   **C# Mustache Engine (Stubble.Core)**: Integrate the `Stubble.Core` NuGet library (DLL ≈140 KB) to manage templates, loop evaluations, and variables mapping without custom Regex maintenance.
*   **DOCX Template Binding (Markdown Rendering Flow)**: Bind Mustache variables first to compile raw text/Markdown. The resultant paragraphs are then converted to structured layouts via `DocxExporter`.
*   **Speaker Alias Global Sync**: The new `speaker_profiles` table tracks `id`, `original_id`, `display_name`, `voiceprint_embedding`, and `created_at`. Renaming a speaker modifies the `display_name` in the database, automatically syncing all future transcript exports.
*   **Embedding Pipeline (Pyannote Embedding Model)**: Run pyannote's speaker embedding extractor model separately on cropped speaker segment WAV cuts. Compute a speech-duration weighted average vector to represent the speaker cluster.
*   **Glossary Cache**: Build `GlossaryCache` dictionary of precompiled patterns. Support sorting dictionary applications sequentially based on domain priority (e.g. medical before finance).
*   **Template Abstraction (`ITemplateEngine`)**: Define `ITemplateEngine` and implement a Stubble engine compiler wrapping Stubble rendering.

---

## 2. Proposed Changes & Task Breakdown

### Component 1: Database Schema & Entity Mappings
Grouped under [Momo.Core/](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Core) and [Momo.Infrastructure/](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Infrastructure).

#### [NEW] [SpeakerProfile.cs](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Core/Entities/SpeakerProfile.cs)
*   Define the core data class for voiceprints:
    ```csharp
    public class SpeakerProfile {
        public string Id { get; set; } = string.Empty;
        public string OriginalId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public byte[] VoiceprintEmbedding { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
    }
    ```

#### [MODIFY] [AppDbContext.cs](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Infrastructure/Db/AppDbContext.cs)
*   Add `DbSet<SpeakerProfile> SpeakerProfiles { get; set; }`.
*   Extend `Job` schema with properties:
    *   `SelectedGlossaries` (string, serialized JSON)
    *   `SelectedRole` (string)
    *   `SelectedTemplate` (string)

#### [MODIFY] [Initializer.cs](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Infrastructure/Db/Initializer.cs)
*   Update database generation queries to create `speaker_profiles` and alter `job_queue` with configuration columns.

---

### Component 2: Python Backend & Pipeline Engines
Grouped under [momo_worker/](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/momo_worker).

#### [NEW] `glossary_processor.py`
*   Implement `GlossaryProcessor` loading selected glossary files from the `glossaries/` workspace directory.
*   Implement regex-based case-insensitive replacements on word boundaries with a compiled regex cache to prevent compilation overhead in execution loops. Support priority-based dictionary ordering (e.g. medical before finance).

#### [MODIFY] `diarizer.py`
*   Integrate Speaker Memory embedding comparisons.
*   Query existing `speaker_profiles` entries.
*   Crop speaker audio segments and extract voiceprint embeddings using `pyannote/embedding`.
*   Calculate duration-weighted average embeddings for speaker clusters.
*   Compare similarity using cosine similarity calculations.
*   If similarity >= threshold (default `0.80`), match speaker to stored `SpeakerProfile.Id`. Otherwise, write a new profile record to the database.

---

### Component 3: Configuration Schemas & C# Template Exporters
Grouped under [Momo.Infrastructure/Exporters/](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.Infrastructure/Exporters).

#### [NEW] `ITemplateEngine.cs` & `StubbleTemplateEngine.cs`
*   Define `ITemplateEngine` interface.
*   Implement `StubbleTemplateEngine` using Stubble.Core NuGet package.

#### [MODIFY] `TxtExporter.cs`, `DocxExporter.cs`
*   Update exporters to accept template file inputs.
*   Compile Markdown template formats before writing structured text (for TXT) or paragraphs (for DOCX).

---

### Component 4: Avalonia Client Dashboard Integration
Grouped under [Momo.App/](file:///d:/Antigravity/Project%203_Enterprise%20Momo/src/Momo.App).

#### [MODIFY] `MainWindow.axaml` & `MainWindow.axaml.cs`
*   Introduce a collapsible settings sidebar on the right side of the dashboard, hidden by default.
*   Load checkbox lists of glossary files, roles, and template options dynamically on startup.

#### [MODIFY] `MainViewModel.cs`
*   Bind UI inputs.
*   Include selected configurations into enqueued `Job` metadata parameters.

---

## 3. Verification & Testing Plan

### Automated Unit Tests
*   `Test_Glossary_Normalization` in `IntegrationTests.cs`:
    *   Verify terminology replacements match boundaries, priority-order overrides, and case-insensitivity rules.
*   `Test_SpeakerMemory_CrossFile` in `IntegrationTests.cs`:
    *   Load pre-recorded 10s audio fixtures of the same speaker from `tests/Fixtures/`.
    *   Validate that the database maps both runs to the same unique profile ID.
*   `Test_Template_Export` in `IntegrationTests.cs`:
    *   Verify that rendering template variables output correct strings under different layouts.

### Manual Verification
1.  Verify the right settings sidebar expands and collapses properly.
2.  Deploy and process a 2-person meeting audio to verify multi-speaker tags are generated correctly in exported files.
