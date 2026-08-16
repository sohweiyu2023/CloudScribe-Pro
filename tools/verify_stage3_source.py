#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from pathlib import Path


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def require_text(root: Path, relative: str, *needles: str) -> str:
    path = root / relative
    if not path.is_file():
        raise ValueError(f"required Stage 3 source file missing: {relative}")
    text = path.read_text(encoding="utf-8-sig")
    for needle in needles:
        if needle not in text:
            raise ValueError(f"{relative} is missing required Stage 3 contract token: {needle!r}")
    return text


def main() -> int:
    root = Path.cwd().resolve()
    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
        global_json = json.loads((root / "global.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"invalid Stage 3 JSON contract: {exc}")

    stage = state.get("current_stage")
    version = str(state.get("repository_version", ""))
    if state.get("project") != "CloudScribe Pro" or stage not in (3, 4):
        return fail("SESSION_STATE.json does not identify a checkpoint that preserves CloudScribe Pro Stage 3")
    if stage == 3 and not version.startswith("0.4.0-stage3"):
        return fail(f"unexpected Stage 3 repository version: {version!r}")
    if stage == 4 and not version.startswith("0.5.0-stage4"):
        return fail(f"unexpected Stage 4 repository version while preserving Stage 3: {version!r}")
    if state.get("required_dotnet_sdk") != "10.0.400" or global_json.get("sdk", {}).get("version") != "10.0.400":
        return fail("Stage 3 must preserve the certified .NET SDK 10.0.400 checkpoint")
    if state.get("stage2_promoted") is not True:
        return fail("Stage 3 source does not record promoted Stage 2")
    if state.get("stage2_manual_visual_acceptance") is not True or state.get("stage2_user_clicked_editor_retest") is not True:
        return fail("Stage 3 source does not retain real-PC Stage 2 visual acceptance")
    if state.get("stage3_slice1_complete") is not True or state.get("stage3_slice2_complete") is not True:
        return fail("certified Stage 3 Slice 1/Slice 2 checkpoints are not recorded complete")
    if state.get("stage3_completion_candidate") is not True:
        return fail("Stage 3 completion-candidate history is not recorded")
    if stage == 3:
        if state.get("stage3_complete") is not False or state.get("stage3_promoted") is not False:
            return fail("Stage 3 completion candidate must not claim Stage 3 complete/promoted")
    else:
        if state.get("stage3_complete") is not True or state.get("stage3_final_windows_certified") is not True or state.get("stage3_promoted") is not True:
            return fail("Stage 4 must preserve complete, final-Windows-certified and promoted Stage 3 state")
        if str(state.get("stage3_final_certification_run")) != "31900688488" or state.get("stage3_promoted_commit") != "beb186bc57f30f3f308e398085bc3af3c94f4020":
            return fail("Stage 4 Stage 3 evidence binding does not match the authoritative promotion")
    if state.get("whole_application_final_claimed") is not False:
        return fail("Stage 3 source incorrectly claims the whole application is final")

    required_files = (
        "src/CloudScribe.Application/Documents/IDocumentLibrary.cs",
        "src/CloudScribe.Application/Documents/DocumentAutosaveCoordinator.cs",
        "src/CloudScribe.Application/Documents/DocumentPreprocessor.cs",
        "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs",
        "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Save.cs",
        "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Import.cs",
        "src/CloudScribe.App/DocumentWindowBehavior.cs",
        "src/CloudScribe.App/MainWindow.Stage3Library.cs",
        "src/CloudScribe.App/Views/DocumentLibraryPanel.axaml",
        "src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs",
        "src/CloudScribe.Infrastructure/Persistence/EfDocumentLibrary.cs",
        "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",
        "src/CloudScribe.Infrastructure/Files/BoundedLocalDocumentImporter.cs",
        "src/CloudScribe.Infrastructure/Files/BoundedDocxTextExtractor.cs",
        "src/CloudScribe.Infrastructure/Files/BoundedHtmlTextExtractor.cs",
        "tests/CloudScribe.Application.Tests/DocumentPreprocessorTests.cs",
        "tests/CloudScribe.Infrastructure.Tests/BoundedLocalDocumentImporterTests.cs",
        "tests/CloudScribe.Infrastructure.Tests/DatabaseRecoveryTests.cs",
        "tests/CloudScribe.Architecture.Tests/Stage3CompletionArchitectureTests.cs",
    )
    for relative in required_files:
        if not (root / relative).is_file():
            return fail(f"required Stage 3 completion file missing: {relative}")

    if (root / "src/CloudScribe.Infrastructure/Files/BoundedLocalDocumentImporterV2.cs").exists():
        return fail("duplicate BoundedLocalDocumentImporterV2.cs remains")
    if (root / "src/CloudScribe.Infrastructure/Persistence/ObservabilityDbContext.cs").exists():
        return fail("legacy ObservabilityDbContext remains")

    try:
        initializer = require_text(root, "src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs",
            "MigrateAsync", "PRAGMA integrity_check", "PRAGMA foreign_key_check",
            "CreateVerifiedPreMigrationBackupAsync", "CheckpointWalAsync", "RestoreBackup", "ClearAllPools")
        if "EnsureCreatedAsync" in initializer:
            return fail("Stage 3 DatabaseInitializer still uses EnsureCreatedAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Persistence/EfDocumentLibrary.cs",
            "IDocumentLibrary", "DocumentConcurrencyException", "ReadVerifiedAsync", "BeginTransactionAsync", "CommitAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",
            "FileMode.CreateNew", "Flush(flushToDisk: true)", "File.Move", "SHA256.HashData", "ValidateExistsWithoutLinks")
        require_text(root, "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.State.cs",
            "LocalDocuments", "DocumentSaveState", "RequiresDocumentSaveBeforeClose", "CreateAsync", "OpenAsync", "SearchAsync")
        require_text(root, "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Save.cs",
            "DocumentAutosaveDebounce", "DocumentRevisionKind.Autosave", "DocumentRevisionKind.Checkpoint",
            "DocumentConcurrencyException", "PrepareDocumentCloseAsync", "your text was not overwritten")
        require_text(root, "src/CloudScribe.App/ViewModels/ShellViewModel.Documents.Import.cs",
            "ILocalDocumentImporter", "DocumentPreprocessor", "ImportDocumentAsync",
            "NormalizeLineEndings: true", "SimplifyUrls: false", "DocumentRevisionKind.Import")
        require_text(root, "src/CloudScribe.App/DocumentWindowBehavior.cs",
            "Key.S", "Key.N", "Key.O", "RequiresDocumentSaveBeforeClose: true",
            "eventArgs.Cancel = true", "PrepareDocumentCloseAsync")
        library = require_text(root, "src/CloudScribe.App/Views/DocumentLibraryPanel.axaml",
            "LOCAL DOCUMENT LIBRARY", "PlaceholderText=\"Search local documents\"",
            "RefreshDocumentLibraryCommand", "ImportDocumentCommand", "NewDocumentCommand", "LocalDocuments")
        if "Watermark=" in library:
            return fail("Stage 3 Library uses obsolete Avalonia Watermark API")
        require_text(root, "src/CloudScribe.App/MainWindow.Stage3Library.cs",
            "LOCAL AUTOSAVE", "DocumentSaveState", "Edits are saved locally with debounced autosave",
            "Ctrl+S creates an explicit checkpoint")
        require_text(root, "src/CloudScribe.Infrastructure/Files/BoundedDocxTextExtractor.cs",
            "MaxArchiveExpandedBytes", "MaxArchiveEntryBytes", "MaxArchiveEntries", "MaxCompressionRatio",
            "DtdProcessing.Prohibit", "XmlResolver = null", "ValidateArchivePath")
        require_text(root, "src/CloudScribe.Infrastructure/Files/BoundedHtmlTextExtractor.cs",
            "IsDiscardedContainer", '"script" or "style"', "SkipContainer")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/BoundedLocalDocumentImporterTests.cs",
            "PlainTextPreservesUnicode", "HtmlIsImportedAsInertText", "DocxRejectsParentTraversalEntry",
            "DocxRejectsDtdDeclarations", "DocxRejectsSuspiciousCompressionRatio", "DeclaredOversizeSourceFailsBeforeReading")
        require_text(root, "tests/CloudScribe.Application.Tests/DocumentPreprocessorTests.cs",
            "IdentityPreviewPreservesUnicodeExactly", "👨‍👩‍👧‍👦", "العربية עברית", "SourceMap")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/DatabaseRecoveryTests.cs",
            "InitializerCreatesVerifiedBackupBeforeUpgradingExistingDatabase",
            "FailedMigrationRestoresVerifiedPreMigrationDatabase",
            "CorruptDatabaseFailsClosedWithoutReplacingOriginalBytes")
        require_text(root, "tests/CloudScribe.Architecture.Tests/Stage3CompletionArchitectureTests.cs",
            "Stage3WorkspaceUsesDurableDocumentStateAndRecoveryAwareShortcuts",
            "Stage3LibraryAndImporterUseCurrentTruthfulAndBoundedContracts",
            "Stage3RecoveryKeepsVerifiedPreMigrationBackupAndFailClosedRestoreEvidence")
    except (OSError, ValueError) as exc:
        return fail(str(exc))

    suffix = "while final promotion remains gated" if stage == 3 else "and the exact promoted Stage 3 evidence is preserved into Stage 4"
    print(f"PASS: Stage 3 has durable local Library/editor autosave/checkpoint/conflict handling, bounded import/preprocessing, Unicode/source-map coverage, and fail-closed migration backup/recovery contracts {suffix}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
