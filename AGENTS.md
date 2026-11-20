# Libzora Agent & Maintainer Guide

Instructions for human contributors and AI coding agents (Claude Code, Codex, Cursor, Copilot, etc.) working on Libzora.

## 1. Start here

- Read [`docs/code-map.md`](docs/code-map.md) first to jump straight to the 2–3 files relevant to your task instead of scanning the whole repository.
- Read [`CONTRIBUTING.md`](CONTRIBUTING.md) for the branch model and PR expectations:
  - **`dev`** is the integration branch. All PRs target `dev`.
  - **`master`** is the protected release branch (triggers `.github/workflows/release.yml`). Never push or PR directly to `master`.

## 2. Architecture & layer rules

Dependency direction is strictly one-way:

```text
Core → Layout → Infrastructure → Application → Reader → App → Desktop.{Windows,MacOS,Linux}
                                                        └─→ Mobile → Mobile.{iOS,Android,Desktop}
```

- `Libzora.Core`: domain models and contracts only. No UI, no I/O.
- `Libzora.Application`: orchestration services only. **Never** reference Avalonia, `Libzora.App`, or any platform head (enforced by tests).
- Platform-specific behaviour belongs behind a `Libzora.Core` contract and is implemented in `Libzora.Platform.*`.
- Keep source files **under ~800 lines**. When a `MainWindow` partial or service grows past that, split along its feature boundary into `TypeName.<Aspect>.cs` and update `docs/code-map.md`.
- **High-risk sync core**: `src/Libzora.Infrastructure/S3SyncService.Database.ApplyRemote.cs` is intentionally kept whole; do not refactor it without dedicated regression tests.

## 3. Security, encryption & privacy invariants

- **Never commit secrets or local machine data**: no API keys, tokens, passwords, signing certificates, provisioning profiles, real user databases, or personal paths/device UDIDs. Always inspect diffs before committing.
- **Zero-knowledge sync & backups**:
  - Sync keys remain protected on the user's device only — never upload them, write them to logs/backups/cloud paths, or add server-side key recovery.
  - Encrypted cloud object paths must not contain plaintext content hashes (use slow-derived, purpose-isolated HMAC naming keys). Preserve `LIBZORA-SYNC3` padding and protocol v6 read compatibility.
  - Full `.libzora` exports/imports must be encrypted and fully authenticated before replacing any local library data.
  - Account regions (CN / International) are strictly isolated: never copy, merge, or fall back across regions.
- **No sensitive/internal copy in public docs**: keep READMEs and user-facing pages focused on product features, installation, and usage — omit internal workflows or operational copy.

## 4. Cross-layer checklists (common pitfalls)

- **Adding or changing a mobile/iOS preference**:
  1. `src/Libzora.Mobile/MobilePreferences.cs`
  2. `src/Libzora.Mobile.iOS/IOSNativeFrontend.cs` (`NormalizePreferences`)
  3. `src/Libzora.Mobile.iOS/NativeUI/NativeBridge.swift` (`struct MobilePreferences`)
  4. `src/Libzora.Mobile/Localization/Strings.en-US.json` (and `Strings.backend.en-US.json` for backend `UiText` keys)
  5. Run `python3 scripts/check-ios-localization.py` (must report `0 errors`) and `bash tests/Libzora.Mobile.iOS.LogicTests/run-tests.sh`.
- **Editing `Libzora.Layout` (EPUB typesetting)**:
  - `ChapterContent.BodyText` offsets are the shared coordinate space for annotations, whole-book search, and TTS. Display-only transforms (casing, synthetic hyphens, list markers, ruby) must never shift source offsets.
  - Run `dotnet test tests/Libzora.Tests/Libzora.Tests.csproj` to verify pagination, selection, footnote, and typography regressions.
- **Editing `Libzora.Reader` (`NativePdfReaderHost` / `PdfReaderLayout`)**:
  - `PdfReaderLayout.Pages` holds full uncropped page bounds (for glyphs, highlights, links, and selection); `Frames` holds the visible clipped sheet (e.g. when auto-crop is active). Keep those distinct so selection/annotation coordinates stay exact.
  - Run PDF suites in both `Libzora.Tests` and `Libzora.App.Tests`.
- **Updating `MainWindow` or iOS UI**:
  - Destructive actions (such as deleting books from context menus) require a confirmation dialog.
  - Update `docs/code-map.md` whenever adding or splitting files.

## 5. Build, test & known gotchas

Toolchain pinned in `global.json`: **.NET SDK 10.0.400**, **Avalonia 12.1.1**.

```bash
# Desktop (pick your host OS)
dotnet build src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj
dotnet build src/Libzora.Desktop.Windows/Libzora.Desktop.Windows.csproj
dotnet build src/Libzora.Desktop.Linux/Libzora.Desktop.Linux.csproj

# Core & UI test suites
dotnet test tests/Libzora.Tests/Libzora.Tests.csproj
dotnet test tests/Libzora.App.Tests/Libzora.App.Tests.csproj

# iOS localization & Swift logic tests (no simulator required)
python3 scripts/check-ios-localization.py
bash tests/Libzora.Mobile.iOS.LogicTests/run-tests.sh
```

Known gotchas:
- **MSBuild `MSB4166` worker crash** during parallel builds/tests: pass `-nodeReuse:false` (and `-p:UseSharedCompilation=false` for iOS builds).
- **iOS simulator**: see [`docs/ios-native-frontend.md`](docs/ios-native-frontend.md). Use `iossimulator-arm64` on Apple Silicon and `iossimulator-x64` on Intel Macs. When updating an existing preview bundle without wiping books, use `tests/Libzora.Mobile.iOS.UITests/prepare-preview.py --preserve-data`.
- **iOS physical device builds**: see [`docs/ios-development.md`](docs/ios-development.md). Incremental `ios-arm64` builds can miss signing top-level `libpdfium.dylib`, causing launch crashes — clean `ios-arm64` `obj`/`bin` before release/device builds and run `codesign --verify --strict` on the `.app`, `libpdfium.dylib`, and `PlugIns/LibzoraWidgets.appex`.
- **Windows self-contained debug builds**: publish with `--self-contained true` and sync into your test directory without deleting `data/` or `backups/`.
- **PR AI review workflow**: `.github/workflows/review.yml` runs incremental PR reviews when the `ANTHROPIC_API_KEY` repository secret is configured.
