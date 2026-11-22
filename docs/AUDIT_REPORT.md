# CloudScribe Pro — Independent System Audit Report

**Repository:** `https://github.com/sohweiyu2023/CloudScribe-Pro`
**Baseline Audited:** PR #129 (`c634e3d7eaf0807db07b70851e88e6e875172699`, tree `9233225d79146741b8ab413f8049dc1115a74738`)
**Upstream Reference:** `main` (`156b723d4b45c2056d0a06badd215abd08f7c829`), PR #130 (`9e3f786d094811301dfd7ceb267ab5d263a6647b`, `.mailmap` only)
**Review Branch:** `review/cloudscribe-system-audit`
**Audit Environment:** Linux x64 (`.NET SDK 10.0.402`, Python 3.11.2, Git 2.39.5; no Windows DPAPI, no `pwsh`, no TerraTron PC access, no live Google Cloud calls)
**Audit Date:** 2026-11-22

---

## 1. Executive Scorecard

| Dimension | Weight | Score (Post-Fix) | Key Assessment |
| --- | ---: | ---: | --- |
| Architecture & Modularity | 15% | 88 / 100 | Clean acyclic layering (`Domain` → `Application` → `Infrastructure` → `App`), strict analyzer/lockfile hygiene; `GoogleTtsUsabilityHotfixBinder` partial-class surface (30+ files) is the main structural hotspot. |
| Data Integrity & Concurrency | 25% | 92 / 100 | Strong SHA-256 revision verification, SQLite WAL + pre-migration backups, spend-approval barriers, and fixed autosave-to-checkpoint self-conflict and faulted-autosave recovery. |
| UI, Usability & Accessibility | 20% | 84 / 100 | Strong keyboard navigation, Focus Reading, high-contrast/DPI matrix, and improved Library search/Clear/screen-reader labels + Studio drag-and-drop; native Windows DPI/NVDA pass still pending. |
| Security, Privacy & Spend Safety | 20% | 94 / 100 | Strict fail-closed spend approval, Windows Credential Manager isolation, content-free trend history, symlink/reparse defenses, and zero automatic paid retries. |
| Testing, CI & Governance | 10% | 90 / 100 | 3,160+ compiled tests + 153 material regression checks + deterministic SHA-256 manifest; fixed Windows NTFS same-tick mutation flake in history reader. |
| Documentation & Operations | 10% | 89 / 100 | Thorough developer, installation, diagnostics, and user guides; consolidated audit and handover docs now added under `docs/`. |
| **Weighted Overall Score** | **100%** | **89.9 / 100** | **Production-grade safety posture; ready for Windows full-suite CI and native candidate acceptance.** |

---

## 2. Independent Assessment of Supplied Findings

| Supplied Item / Premise | Verification Result | Independent Assessment |
| --- | --- | --- |
| PR #129 (`c634e3d7...`) is the authoritative app baseline | Confirmed | Verified exact commit hash, tree hash (`9233225d...`), and parent (`main` `156b723d...`). Includes Library Refresh failure status isolation, startup `Loading` → `Error` recovery, missing-document `IsDocumentSaving` reset, and same-process `GoogleTrendHistoryLoggerProvider` writer guard (`HistoryOwners`). |
| PR #130 (`9e3f786d...`) failed Windows hosted CI on same-size history reader mutation assertion | Confirmed & Fixed | PR #130 only adds `.mailmap` (4 lines) + `SHA256SUMS.txt`. The real underlying defect is in `tools/review_google_structure_history.py` (`snapshot_lines`): on Windows NTFS, rapid same-size in-place overwrites (`row(200)` → `row(400)` in `test_in_place_source_change_fails_without_path_disclosure`) complete within one ~15.625 ms OS clock tick, leaving `st_dev`, `st_ino`, `st_size`, `st_mtime_ns`, `st_mode`, and `st_ctime_ns` identical. Fixed via bounded O(1)-memory `blake2b` tail-window digest verification without weakening any test assertion. |
| PR #129 `OpenDocumentCoreAsync` missing-document handling | Partially Incomplete — Fixed | PR #129 fixed `IsDocumentSaving = false` and `DocumentSaveState = "Open failed"`, but left `if (LocalDocuments.Count == 0) ClearDocumentWorkspace();` unguarded against an active `LibrarySearchQuery` filter (0 search matches != empty library). Fixed. |
| `MainWindow.axaml` literal `"STAGE 2 PREVIEW"` / `"UNSAVED PREVIEW"` strings | Rejected as XAML Edit (By Design) | Initial inspection flagged these strings, but cross-referencing `MainWindow.Stage3Library.cs`, `FinalReleaseIntegrationContractTests.cs`, and `tools/verify_stage2_source.py` proved they are intentionally replaced at runtime by `ReplaceLegacyStage2WorkspaceCopy` to preserve the Stage 2 source-verifier contract. Left intact; fixed a real `PropertyChanged` memory leak in `MainWindow.Stage3Library.cs` instead. |
| Native Windows candidate/installer acceptance gate | Unverified (Environment Boundary) | This Linux VM has no Windows UI, DPAPI, `pwsh`, or access to TerraTron PC. Native packaging, installer, DPI, and screen-reader acceptance remain explicitly required on Windows. |

---

## 3. Complete Issue & Improvement Inventory

### A. `[Implemented & Verified]`

1. **AUD-001 (High — CI / Data Integrity): Windows NTFS Same-Tick Same-Size Mutation Detection in History Reader**
   - **Location:** `tools/review_google_structure_history.py`, `tools/test_review_google_structure_history_streaming.py`
   - **Root Cause:** `snapshot_lines` checked only `fstat`/`lstat` metadata fields, which do not change when a file is overwritten in-place with identical byte length within one ~15.625 ms Windows timer tick.
   - **Fix:** Added `_tail_digest(stream, start, end)` computing a bounded 16-byte `blake2b` digest of the newest ≤64 KiB in the scan window before and after `reverse_lines` (O(1) memory, O(1) I/O). Added deterministic unit test `test_same_size_same_metadata_in_place_mutation_is_rejected` while keeping `test_in_place_source_change_fails_without_path_disclosure` untouched.
   - **Verification:** `test_review_google_structure_history.py` (5/5 OK) and `test_review_google_structure_history_streaming.py` (13/13 OK).

2. **AUD-002 (High — Data Integrity / Concurrency): Faulted Autosave Blocking Checkpoint/Dispose & In-Flight Autosave Self-Conflict**
   - **Location:** `src/CloudScribe.Application/Documents/DocumentAutosaveCoordinator.cs`, `tests/CloudScribe.Application.Tests/DocumentAutosaveCoordinatorTests.cs`
   - **Root Cause:** `SaveCheckpointAsync` and `DisposeAsync` executed `_ = await pending`, which re-threw a previously faulted autosave exception (blocking manual Ctrl+S retry and disposal) and discarded a just-completed same-document autosave snapshot (causing a false `DocumentConcurrencyException` on Ctrl+S).
   - **Fix:** Added `DrainPendingAutosaveAsync` to safely drain pending autosaves without propagating already-observed failures, and tracked `_lastCompletedAutosave` `(DocumentId, ConcurrencyVersion)` so `SaveCheckpointAsync` advances `ExpectedConcurrencyVersion` for its own completed autosave while still failing closed on external writers.
   - **Verification:** `CloudScribe.Application.Tests` 524/524 PASS (including `FaultedPendingAutosaveDoesNotBlockCheckpointOrDispose` and `CompletedInFlightAutosaveAdvancesCheckpointConcurrencyVersion`).

3. **AUD-003 (High — Concurrency): Cross-Document Stale Autosave Observer Guard**
   - **Location:** `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Save.cs`, `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs`
   - **Root Cause:** `ApplyDocumentSnapshot`, `ClearDocumentWorkspace`, and `SaveCurrentDocumentCoreAsync` did not increment `_autosaveSequence`, and `ObserveDocumentAutosaveAsync` did not verify `snapshot.Id == _currentDocumentId`.
   - **Fix:** Incremented `_autosaveSequence` on document switch/clear/checkpoint and added `snapshot.Id != _currentDocumentId` guard in `ObserveDocumentAutosaveAsync`.
   - **Verification:** `CloudScribe.Architecture.Tests` `ShellViewModelStateTests` PASS (including autosave recovery and checkpoint suites).

4. **AUD-004 (Medium — UX / State Integrity): Active Search Filter Wrongly Clearing Workspace on Missing Document Open**
   - **Location:** `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs`, `tests/CloudScribe.Architecture.Tests/ShellViewModelStateTests.AuditRegressions.cs`
   - **Root Cause:** `OpenDocumentCoreAsync` checked `if (LocalDocuments.Count == 0) ClearDocumentWorkspace();` without checking whether `LibrarySearchQuery` was active.
   - **Fix:** Guarded with `if (LocalDocuments.Count == 0 && string.IsNullOrWhiteSpace(LibrarySearchQuery))`.
   - **Verification:** `MissingDocumentOpenDuringActiveSearchFilterDoesNotClearWorkspace` PASS.

5. **AUD-005 (Medium — UX / State Integrity): Failed Library Pagination Corrupted `LibraryPageOffset`**
   - **Location:** `src/CloudScribe.App/ViewModels/ShellViewModel.LibraryPaging.cs`, `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs`
   - **Root Cause:** `PreviousLibraryPageAsync` and `NextLibraryPageAsync` mutated `LibraryPageOffset` before awaiting `RefreshDocumentLibraryObservedAsync()`.
   - **Fix:** Added `int? requestedOffset = null` parameter to `RefreshDocumentLibraryCoreAsync` / `RefreshDocumentLibraryObservedAsync` so `LibraryPageOffset` only updates inside `ApplyLibraryPage` on success.
   - **Verification:** Strict Release build + `ShellViewModelStateTests` PASS.

6. **AUD-006 (Medium — UI / Accessibility): Library Clear-Search Button, Descriptive Screen-Reader Labels, Refresh Loading State & Pagination Spacing**
   - **Location:** `src/CloudScribe.App/Views/DocumentLibraryPanel.axaml`, `src/CloudScribe.App/ViewModels/LibraryArticleViewModel.cs`, `src/CloudScribe.App/ViewModels/LibraryPartViewModel.cs`, `src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs`
   - **Root Cause:** All `Open article` / `Open part` buttons had identical generic accessible names; no Clear search button existed despite UI copy telling users to clear the search; `Refresh` lacked `AutomationProperties.Name` and stayed enabled during loading; pagination buttons had no spacing.
   - **Fix:** Added `OpenAccessibleName` on `LibraryArticleViewModel` and `LibraryPartViewModel`, added `ClearLibrarySearchCommand` + `HasLibrarySearchQuery` + `Clear` button, added `AutomationProperties.Name` and `!IsLibraryPageLoading` binding on `Refresh`, and added horizontal button margins.
   - **Verification:** `ClearLibrarySearchCommandResetsQueryAndState` and `LibraryArticleAndPartExposeDescriptiveOpenAccessibleNames` PASS.

7. **AUD-007 (Medium — Accessibility / UX): Status Bar Live Announcement & Hover Tooltip for Truncated Messages**
   - **Location:** `src/CloudScribe.App/MainWindow.axaml`
   - **Root Cause:** Bottom `StatusMessage` `TextBlock` used `TextTrimming="CharacterEllipsis"` with no `ToolTip.Tip` and no `AutomationProperties.LiveSetting="Polite"`.
   - **Fix:** Added `ToolTip.Tip="{Binding StatusMessage}"` and `AutomationProperties.LiveSetting="Polite"`.
   - **Verification:** `verify_stage2_source.py` PASS, Release build 0 warnings, `CloudScribe.Architecture.Tests` UI contract tests PASS.

### B. `[Implemented - Needs Verification]` (Compiled & Format-Checked; Requires Native Windows UI Verification)

8. **AUD-008 (High — UI Stability): Window Close Lockout Guard in `DocumentWindowBehavior.CompleteCloseAsync`**
   - **Location:** `src/CloudScribe.App/DocumentWindowBehavior.cs`
   - **Root Cause:** `_closeInProgress = true` was not protected by `try/catch/finally`; an unexpected exception in `PrepareDocumentCloseAsync()` would leave the window permanently unclosable.
   - **Fix:** Wrapped `CompleteCloseAsync` in `try/catch/finally` resetting `_closeInProgress = false` and reporting safe failure status; removed redundant checks in `OnClosing`.
   - **Needs:** Native Windows close-with-dirty-document smoke test.

9. **AUD-009 (Medium — Usability): Studio Drag-and-Drop Single-File Document Import**
   - **Location:** `src/CloudScribe.App/DocumentWindowBehavior.cs`
   - **Root Cause:** Drag-and-drop only worked for Batch TXT files on Queue surfaces; dropping `.txt`, `.md`, `.markdown`, `.html`, `.htm`, or `.docx` onto Studio did nothing.
   - **Fix:** Enabled `DragDrop.AllowDrop` on `WorkspaceScrollViewer` when `IsStudioSelected` is true, routing single supported files safely through `viewModel.ImportDocumentAsync` (auto-checkpointing dirty edits, preserving existing docs) and guiding multi-file drops to Batch MP3.
   - **Needs:** Native Windows File Explorer drag-and-drop test into Studio and Empty Workspace.

10. **AUD-010 (Medium — Memory Leak): Unsubscribed `PropertyChanged` Handler in `MainWindow.Stage3Library.cs`**
    - **Location:** `src/CloudScribe.App/MainWindow.Stage3Library.cs`
    - **Root Cause:** Anonymous lambda attached to `viewModel.PropertyChanged` was never unsubscribed on `window.Closed`.
    - **Fix:** Stored handler reference and unsubscribed `viewModel.PropertyChanged` and `window.Opened` in a one-shot `window.Closed` handler.
    - **Needs:** Native window lifecycle check.

11. **AUD-011 (Medium — Crash Prevention): Guarded Unprotected `async void` Event Handlers**
    - **Location:** `src/CloudScribe.App/Composition/GoogleTtsUsabilityHotfixBinder.CreateMp3.cs`, `src/CloudScribe.App/Composition/GoogleTtsUsabilityHotfixBinder.Batch.Entry.cs`
    - **Root Cause:** `setups.SelectionChanged` (`await reloadVoices()`) and Queue `button.Click` (`await OpenFromQueueAsync(null)`) lacked `try/catch` inside `async void` delegates, risking process crash on UI thread exceptions.
    - **Fix:** Added non-fatal `try/catch` guards reporting safe status messages.
    - **Needs:** Native Windows dialog interaction check.

### C. `[Identified - Not Implemented]` (Prioritized Future Improvements)

12. **AUD-012 (Medium — Architecture / Maintainability): Decompose `GoogleTtsUsabilityHotfixBinder` Partial Class (30+ files, ~5,000+ LOC)**
    - **Observation:** `GoogleTtsUsabilityHotfixBinder` has grown into a monolithic partial-class hub owning 30+ UI dialogs, event subscriptions, and `async void` handlers. Refactoring into discrete view/controller classes with a shared `ObserveUiAsync` helper would eliminate the remaining unprotected `async void` handlers across secondary dialogs without changing user workflows. Deferred to avoid a large architectural rewrite during a stabilization review.

13. **AUD-013 (Medium — Window Close Protection): Extend Close Guard to Active Batch / Long-Audio / Whole-Article Runs**
    - **Observation:** `DocumentWindowBehavior.OnClosing` guards `GenerateWithGoogleCommand.IsRunning` (single-request generation), while Batch/Long-Audio dialogs rely on modal ownership. Exposing a unified `IsAnyProviderRunActive` flag on `ShellViewModel` would make window-close protection uniform across all execution modes.

14. **AUD-014 (Low — UI Polish): Responsive Narrow-Width Header Wrapping in `DocumentLibraryPanel.axaml`**
    - **Observation:** The top header `Grid` (`*,Auto,Auto,Auto`) in `DocumentLibraryPanel.axaml` can crowd the title column at minimum window width (480px) + 200% text scale. Moving the action buttons into a `WrapPanel` row below the title on narrow viewports would improve legibility at 200% DPI.

15. **AUD-015 (Low — Cross-Platform Testing): `CloudScribe.Infrastructure.Tests` `SQLitePCLRaw.core` Assembly Loading on Linux**
    - **Observation:** On Linux x64, `CloudScribe.Architecture.Tests` loads SQLite cleanly, while `CloudScribe.Infrastructure.Tests` threw `FileNotFoundException: SQLitePCLRaw.core` for SQLite-backed tests (non-SQLite tests like `GoogleTrendHistoryLoggerTests` and `BoundedLocalDocumentImporterTests` passed 24/24). Windows CI (`windows-2025`) is unaffected.

### D. `[Rejected/No Action Needed]`

16. **AUD-016: Removing `"STAGE 2 PREVIEW"` / `"UNSAVED PREVIEW"` literals from `MainWindow.axaml`**
    - **Reason:** Intentionally retained for `tools/verify_stage2_source.py` and `FinalReleaseIntegrationContractTests.cs`; replaced at runtime by `MainWindow.Stage3Library.cs`.
17. **AUD-017: Folding PR #130 (`.mailmap`) into this branch**
    - **Reason:** Per explicit instructions, authorship display mapping stays separate from the app review branch.

---

## 4. Verification Summary

| Suite / Check | Result | Notes |
| --- | --- | --- |
| `dotnet format CloudScribe.sln --no-restore --verify-no-changes` | **PASS** | 0 formatting diffs |
| `dotnet build CloudScribe.sln -c Release --no-restore` | **PASS** | 0 warnings, 0 errors (`.NET 10.0.402`) |
| `CloudScribe.Domain.Tests` | **149 / 149 PASS** | 0 failed, 0 skipped |
| `CloudScribe.Application.Tests` | **524 / 524 PASS** | Includes 2 new autosave coordinator tests |
| `CloudScribe.Architecture.Tests` | **668 / 687 PASS** | All 3 new audit regression tests PASS; 19 failures are Windows-only DPAPI (15) or missing `pwsh` (4) |
| `CloudScribe.Infrastructure.Tests` (non-SQLite focused) | **24 / 24 PASS** | `GoogleTrendHistoryLoggerTests` (12/12), `BoundedLocalDocumentImporterTests` (12/12) |
| `tools/test_review_google_structure_history*.py` | **18 / 18 PASS** | Includes new deterministic same-size/same-metadata test |
| `tools/verify_project_dependencies.py` | **PASS** | Acyclic layering & 9 locked projects verified |
| `tools/verify_stage1_source.py` & `verify_stage2_source.py` | **PASS** | Stage 1 & Stage 2 contracts preserved |
| `tools/update_sha256_manifest.py --check` | **PASS** | Verified after freezing docs and source |
| `tools/run_python_regression_shards.py --all` | **153 / 153 PASS** | Verified after manifest regeneration |
| `tools/verify_repository.py` | **PASS** | Full repository governance verified |
| Windows native build, installer & UI acceptance | **PENDING (Windows Required)** | Must run on Windows (`windows-2025` CI + TerraTron disposable profile) |
