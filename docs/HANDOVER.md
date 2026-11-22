# CloudScribe Pro — Complete System Handover

**Repository:** `https://github.com/sohweiyu2023/CloudScribe-Pro`
**Authoritative App Baseline:** PR #129 (`c634e3d7eaf0807db07b70851e88e6e875172699`)
**Review Branch:** `review/cloudscribe-system-audit`
**Public Stable Rollback:** 1.0.8 (`3a383752f0fb2fe95f3ce577f89caef080fdb5e3`)
**Last Updated:** 2026-11-22

---

## 1. System Overview

CloudScribe Pro is a **local-first Windows desktop studio** (Avalonia UI on .NET 10) for drafting, importing, organizing, and narrating long-form text into verified MP3 audiobooks using Google Cloud Text-to-Speech (Standard, WaveNet, Neural2, Studio, Chirp3-HD, and Long Audio Synthesis).

### Core Architectural Principles
- **Local-First & Offline-Capable:** Document creation, editing, debounced autosave, explicit checkpoints, search, library pagination, and URL-omission narration copies work with zero network access or provider credentials.
- **Fail-Closed Spend Safety:** Every billable provider call requires an exact compiled request, verified current catalog/pricing evidence, explicit single-use user spend approval, and durable authorization persistence. No automatic paid retries exist.
- **Privacy & Secret Isolation:** Service-account keys are stored in Windows Credential Manager (never in logs, crash bundles, or SQLite). Trend history (`data/google-trend-history-v1.jsonl`) is strictly content-free structured metadata.
- **Acyclic Clean Architecture:**
  - `src/CloudScribe.Domain`: Pure domain entities, exact decimal money/pricing engine, safety/redaction policies, accessibility preferences, and backup/restore manifest models.
  - `src/CloudScribe.Providers.Abstractions`: Provider contracts (`IGenerationProvider`, `IProviderAdapter`, `IVoiceLabProviderAdapter`, descriptors, capability/quota snapshots).
  - `src/CloudScribe.Application`: Use-case coordinators (`DocumentAutosaveCoordinator`, `DocumentPreprocessor`, `NarrationUrlOmission` v1/v2/v3, `BatchQueueRunner`, `BatchAuthorityEvaluator`, Chirp/sentence planners, logging contracts).
  - `src/CloudScribe.Infrastructure`: SQLite/EF Core persistence (`CloudScribeDbContext`, `EfDocumentLibrary`, `DatabaseInitializer`), SHA-256 verified `DocumentContentStore`, bounded local importers (TXT/MD/HTML/DOCX), `WindowsCredentialVault`, Google TTS/Long-Audio HTTP adapters, `GoogleTrendHistoryLoggerProvider`, and `SupportBundleService`.
  - `src/CloudScribe.App`: Avalonia 12 desktop shell (`MainWindow`, `DocumentLibraryPanel`, `ShellViewModel` partial classes, `DocumentWindowBehavior`, `CompositionRoot`, and `GoogleTtsUsabilityHotfixBinder` dialogs).

---

## 2. Tech Stack & Environment

| Component | Exact Requirement / Version | Notes |
| --- | --- | --- |
| .NET SDK | **`10.0.402` exact** | `global.json` sets `"rollForward": "disable"`. Never relax the pin. |
| Language & TFM | C# 14 / `net10.0` | `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `Meziantou.Analyzer`. |
| UI Framework | Avalonia 12 (Fluent + custom `CosmicThemeCatalog`) | Headless UI tests use `Avalonia.Headless 12.1.4` (test-only). |
| Persistence | SQLite + EF Core (`WAL` mode, `foreign_keys=ON`, `busy_timeout=5000`) | Pre-migration verified backups written to `backups/` before `MigrateAsync`. |
| Test Runner | `Microsoft.Testing.Platform` (xUnit) | Use `dotnet test --solution CloudScribe.sln` or `--project ...`. |
| Scripting & Verifiers | Python 3 (3.13 recommended on CI) & PowerShell 7 (`pwsh`) | Standard library for verifiers; optional schema check needs `jsonschema[format-nongpl]==4.26.0`, `rfc8785==0.1.4`, `cryptography==49.0.0` in an external venv. |
| NuGet Packaging | Central Package Management (`Directory.Packages.props`) + locked restore (`packages.lock.json`) | Always run `dotnet restore CloudScribe.sln --locked-mode --disable-parallel --configfile NuGet.config`. |

### Important Governance Files
- `SESSION_STATE.json`: Intentionally retains Stage8 `1.0.0` governance metadata while the servicing app is `1.0.x`. Do not alter its version to match the app version.
- `SHA256SUMS.txt`: Deterministic inventory of all tracked source/doc/control files. Must be regenerated with `python -B tools/update_sha256_manifest.py` and verified with `--check` after freezing changes.
- `controls/v2.22/clean-carrier/*.b64`: All 11 authenticated pricing control carrier parts are mandatory inputs.

---

## 3. Operations: Build, Test, Package & Install

### Local Developer Checks (Windows PowerShell 7)
```powershell
python -B tools/update_sha256_manifest.py --check
dotnet restore CloudScribe.sln --locked-mode --disable-parallel --configfile NuGet.config
dotnet format CloudScribe.sln --no-restore --verify-no-changes
dotnet build CloudScribe.sln -c Release --no-restore --disable-build-servers -m:1 -nodeReuse:false -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet test --solution CloudScribe.sln -c Release --no-build --no-restore --output Detailed
```

### Python Governance, History & Regression Checks
```powershell
python -B tools/verify_repository.py
python -B tools/verify_project_dependencies.py
python -B tools/verify_stage1_source.py
python -B tools/verify_stage2_source.py
python -B tools/run_python_regression_shards.py --all
python -B tools/run_verifier_self_tests.py
python -B -m unittest discover -s tools -p "test_review_google_structure_history*.py" -v
```

### Windows Packaging, Safe Installation & Rollback
- **Never overwrite** the stable TerraTron executable (`C:\Users\cyber\AppData\Local\Programs\CloudScribe Pro\app\CloudScribe.exe`) during candidate testing.
- **Publish & package** into a new empty physical directory outside the repo using `BUILDING-WINDOWS.txt` and `scripts/New-CleanWindowsShare.ps1`.
- **Install/upgrade** via `scripts/Install-CloudScribe.ps1` (preserves profile, credentials, and stable shortcut path).
- **Test candidates** using a disposable profile directory and offline synthetic fixtures only. Never distribute profiles, databases, service-account JSONs, recordings, or rollback folders.

---

## 4. Core Logic & Integrations

### Document Lifecycle & Autosave
- Documents are stored in SQLite (`Documents` + `DocumentRevisions`) paired with immutable SHA-256 verified UTF-8 files in `DocumentContentStore`.
- `DocumentAutosaveCoordinator` debounces rapid edits (750 ms) into `DocumentRevisionKind.Autosave` while explicit saves (`Ctrl+S`, pre-open, pre-new, pre-import, window close) issue `DocumentRevisionKind.Checkpoint`.
- **Post-Audit Safeguards:** Faulted pending autosaves no longer block manual checkpoints or disposal; an in-flight/just-completed autosave advances `ExpectedConcurrencyVersion` for its own document checkpoint; `ShellViewModel` retires superseded autosave observers on document switch/clear/checkpoint and guards `snapshot.Id == _currentDocumentId`.

### URL Omission Policies (`url-omission-v1` / `v2` / `v3`)
- `NarrationUrlOmission.Transform(text, version)` dispatches immutable policies. New preparations use `url-omission-v3` (stricter bare-domain acceptance to avoid deleting prose like `awareness.In` or `end.in`). Older saved narration copies and settings replay their recorded version byte-for-byte.

### Google TTS & Spend Governance
- **Single-Request MP3:** `Create MP3` dialog (`GoogleTtsUsabilityHotfixBinder.CreateMp3.cs`) → verify catalog → `Review cost (no audio)` → explicit checkbox approval → `Generate MP3` via Stage6 authorized runtime chain.
- **Whole-Article / Chirp & Long Audio:** Articles ≥4,000 UTF-8 bytes route to complete-article planning/review or Long Audio synthesis, with durable claim barriers, failed-range subdivision, and verified MP3 assembly.
- **Batch MP3:** Drag-and-drop or browse TXT files/folders on Queue surfaces or Batch dialog; supports natural ordering, failure policy (`SkipAndContinue` / `StopBatch`), URL omission, and durable draft/queue persistence.

### Structured Diagnostics & Trend History
- `GoogleTrendHistoryLoggerProvider` writes content-free JSONL telemetry to `data/google-trend-history-v1.jsonl` with same-process exclusive writer ownership (`HistoryOwners`, added in PR #129).
- `tools/review_google_structure_history.py` performs bounded reverse-streaming offline analysis with API-local `fstat`/`lstat` checks **plus** a bounded ≤64 KiB `blake2b` tail-digest check (added in this audit to catch same-tick NTFS in-place overwrites).

---

## 5. Audit & Change Log (`review/cloudscribe-system-audit`)

### Modified Files
| File | Change Summary |
| --- | --- |
| `tools/review_google_structure_history.py` | Added `_tail_digest` (bounded ≤64 KiB `blake2b`) check in `snapshot_lines` to deterministically reject same-size in-place mutations even within one Windows NTFS timer tick. |
| `tools/test_review_google_structure_history_streaming.py` | Added `test_same_size_same_metadata_in_place_mutation_is_rejected` (preserved original assertion unchanged). |
| `src/CloudScribe.Application/Documents/DocumentAutosaveCoordinator.cs` | Added `DrainPendingAutosaveAsync` and `_lastCompletedAutosave` tracking so faulted autosaves don't block checkpoints/disposal and in-flight autosaves don't cause false self-conflicts. |
| `tests/CloudScribe.Application.Tests/DocumentAutosaveCoordinatorTests.cs` | Added 2 regression tests for faulted pending autosave recovery and in-flight autosave concurrency advancement. |
| `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs` | Guarded missing-doc workspace clear against active `LibrarySearchQuery`; added `ClearLibrarySearchCommand` & `HasLibrarySearchQuery`; added `requestedOffset` parameter for safe paging; incremented `_autosaveSequence` on snapshot apply/clear. |
| `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Save.cs` | Incremented `_autosaveSequence` on checkpoint start and guarded `ObserveDocumentAutosaveAsync` with `snapshot.Id == _currentDocumentId`. |
| `src/CloudScribe.App/ViewModels/ShellViewModel.LibraryPaging.cs` | Deferred `LibraryPageOffset` update until `RefreshDocumentLibraryObservedAsync` succeeds. |
| `src/CloudScribe.App/ViewModels/LibraryArticleViewModel.cs` | Added `OpenAccessibleName` (`"Open article: {Title}"`). |
| `src/CloudScribe.App/ViewModels/LibraryPartViewModel.cs` | Added `OpenAccessibleName` (`"Open part: {Title}"`). |
| `src/CloudScribe.App/Views/DocumentLibraryPanel.axaml` | Added Clear search button, `AutomationProperties.Name` + loading disable on `Refresh`, descriptive `OpenAccessibleName` bindings, and pagination button spacing. |
| `src/CloudScribe.App/DocumentWindowBehavior.cs` | Added safe single-file drag-and-drop import on Studio (`WorkspaceScrollViewer`); wrapped `CompleteCloseAsync` in `try/catch/finally` to prevent window-close lockout. |
| `src/CloudScribe.App/MainWindow.Stage3Library.cs` | Unsubscribed `viewModel.PropertyChanged` and `window.Opened` on `window.Closed` to fix `MainWindow` memory leak. |
| `src/CloudScribe.App/MainWindow.axaml` | Added `ToolTip.Tip="{Binding StatusMessage}"` and `AutomationProperties.LiveSetting="Polite"` to bottom status bar `TextBlock`. |
| `src/CloudScribe.App/Composition/GoogleTtsUsabilityHotfixBinder.CreateMp3.cs` | Guarded `async void` `setups.SelectionChanged` voice reload with non-fatal `try/catch`. |
| `src/CloudScribe.App/Composition/GoogleTtsUsabilityHotfixBinder.Batch.Entry.cs` | Guarded `async void` Queue `button.Click` with non-fatal `try/catch`. |
| `SHA256SUMS.txt` | Regenerated and verified (`--check`) after freezing source and docs. |

### Created Files
| File | Purpose |
| --- | --- |
| `tests/CloudScribe.Architecture.Tests/ShellViewModelStateTests.AuditRegressions.cs` | Regression tests for active-search missing-doc handling, `ClearLibrarySearchCommand`, and descriptive library accessible names. |
| `docs/AUDIT_REPORT.md` | Complete independent system audit, scorecard, root causes, and categorized verification matrix. |
| `docs/HANDOVER.md` | Self-contained operational and architectural handover + Master Handover Prompt. |

---

## 6. Risks, Limitations & Prioritized Next Steps

1. **Gate 1 (Immediate — Required Before Merge to `main`):** Apply the review patch/branch on Windows (or push to PR #129), run the complete Windows CI suite (`dotnet test --solution CloudScribe.sln -c Release`, PowerShell self-tests, and `tools/completion_workflow` discovery tests), and confirm the 19 Windows-only DPAPI/`pwsh` tests plus `CloudScribe.Infrastructure.Tests` pass green.
2. **Gate 2 (Immediate — Required Before Release):** Build and package a clean Windows candidate (`scripts/New-CleanWindowsShare.ps1`) into a disposable directory, record its SHA-256, and run native UI acceptance on a disposable profile (verifying Studio drag-and-drop import, Library Clear search, NVDA/Narrator reading of `"Open article: <Title>"` and status-bar live announcements, 125%–200% DPI scaling, and clean upgrade/rollback without touching stable 1.0.8).
3. **Next Engineering Priority:** Refactor `GoogleTtsUsabilityHotfixBinder` (30+ partial files) to route all `async void` UI event handlers through a single guarded dispatcher helper and unify window-close protection across single, batch, whole-article, and long-audio runs.
4. **Separate PR #130 Track:** Once the history reader tail-digest fix from this branch reaches `main`, rebase/re-run PR #130 (`.mailmap`) so its Windows hosted CI passes cleanly without altering `.mailmap` scope.

---

## 7. Master Handover Prompt

Copy the block below to onboard the next AI agent or developer with full context:

```markdown
You are taking over CloudScribe Pro (`https://github.com/sohweiyu2023/CloudScribe-Pro`), a local-first .NET 10 / C# 14 / Avalonia 12 Windows desktop studio for drafting, organizing, and narrating text into verified MP3 audiobooks via Google Cloud Text-to-Speech.

### Authoritative Context & Files to Read First
1. Read `AGENTS.md`, `docs/AUDIT_REPORT.md`, `docs/HANDOVER.md`, and `docs/DEVELOPMENT.md`.
2. Baseline app development branch: PR #129 (`c634e3d7eaf0807db07b70851e88e6e875172699`) plus the audit/fix branch `review/cloudscribe-system-audit` (or its exported patch `CloudScribe-PR129-SystemAudit-Fixes.patch`).
3. Do NOT base new work on public stable 1.0.8 (`3a383752...`) or PR #130 (`9e3f786d...`, display-only `.mailmap`). Never overwrite the stable TerraTron executable (`C:\Users\cyber\AppData\Local\Programs\CloudScribe Pro\app\CloudScribe.exe`) or touch real user profiles/credentials/secrets.

### What Has Already Been Implemented & Verified (Linux x64, .NET 10.0.402)
- Fixed Windows NTFS same-tick same-size mutation detection in `tools/review_google_structure_history.py` via bounded <=64 KiB `blake2b` tail-digest verification (+ deterministic test).
- Fixed `DocumentAutosaveCoordinator` so faulted pending autosaves never block `SaveCheckpointAsync`/`DisposeAsync` and in-flight/completed autosaves advance `ExpectedConcurrencyVersion` instead of throwing false concurrency conflicts (+ 2 unit tests).
- Fixed cross-document stale autosave race in `ShellViewModel.Documents.Save.cs` / `State.cs`, active-search-filter missing-doc workspace wipe, and pre-refresh `LibraryPageOffset` mutation.
- Fixed `MainWindow.Stage3Library.cs` `PropertyChanged` memory leak, `DocumentWindowBehavior.CompleteCloseAsync` missing `try/finally` lockout risk, and unprotected `async void` handlers in `CreateMp3.cs` and `Batch.Entry.cs`.
- Added safe single-file Studio drag-and-drop import (`.txt`, `.md`, `.markdown`, `.html`, `.htm`, `.docx`), Library `Clear` search button, descriptive screen-reader `OpenAccessibleName` labels on Library articles/parts, Refresh loading state, pagination spacing, and status-bar `ToolTip` + `AutomationProperties.LiveSetting="Polite"`.
- Verified: `dotnet format --verify-no-changes` PASS, strict Release build 0 warnings/0 errors, Domain 149/149 PASS, Application 524/524 PASS, Architecture 668/687 PASS (19 Windows DPAPI/`pwsh`-only), History streaming 18/18 PASS, `run_python_regression_shards.py --all` 153/153 PASS, `verify_repository.py` PASS, `SHA256SUMS.txt` regenerated and verified.

### Your Immediate Objectives
1. On Windows (`windows-2025` CI or TerraTron PC with .NET SDK `10.0.402` exact and `pwsh` 7+):
   - Apply/checkout `review/cloudscribe-system-audit`, run the complete `dotnet test --solution CloudScribe.sln -c Release --no-build --no-restore`, PowerShell self-tests (`docs/DEVELOPMENT.md` §5), and `tools/completion_workflow` unittest discovery.
   - Build and package a clean Windows candidate (`scripts/New-CleanWindowsShare.ps1`) outside the repo, record its SHA-256, and complete native UI acceptance on a disposable profile (Studio drag-and-drop, Library Clear search, screen-reader labels, 125%–200% DPI, dirty-close recovery, and offline MP3 fixtures).
2. Only after all Windows CI checks and native acceptance pass, push/update PR #129 and merge to `main` (without force-push, history rewrites, or public Cloudflare R2/Blogger deployment unless explicitly authorized).
3. Keep all strict guardrails intact: exact SDK `10.0.402`, locked NuGet restore, `SESSION_STATE.json` Stage8 1.0.0 governance, zero automatic paid retries, no live provider calls in tests, and `tools/update_sha256_manifest.py --check` before every commit.
```
