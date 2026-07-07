## [5.0.0-beta1-fix1] - 2026-07-07

### Phase β1-fix1 — Path Refactoring & Offline Packaging

This patch release resolves runtime hardcoded path blockers on non-development machines and bundles offline FFmpeg dependencies.

### Added
- **Bundled Offline FFmpeg** (`beta1-fix1`): Included `ffmpeg.exe` and `ffprobe.exe` binaries directly in a nested `ffmpeg/` subdirectory inside the application package.
- **LZMA2 Ultra Compression** (`beta1-fix1`): Upgraded Inno Setup configuration to `lzma2` compression with Solid Compression enabled, optimizing the bundle package footprint.

### Fixed
- **Relative Path Resolution** (`beta1-fix1`): Refactored all C# and Python worker absolute file references (`d:\Antigravity\...`) to use `AppDomain.CurrentDomain.BaseDirectory` and relative lookups, allowing installation and execution from any target directory.
- **XAML Brand Logo Pathing** (`beta1-fix1`): Corrected dynamic relative path loading for brand logos to prevent loading failure under non-root execution contexts.
- **Conforming Path Verification Tests** (`beta1-fix1`): Added `PathResolutionTests.cs` to verify C# database, Python script, and executable relative resolution and safe parallel test execution.

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