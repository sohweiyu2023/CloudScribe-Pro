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

    if state.get("project") != "CloudScribe Pro" or state.get("current_stage") != 3:
        return fail("SESSION_STATE.json does not identify CloudScribe Pro Stage 3")
    if not str(state.get("repository_version", "")).startswith("0.4.0-stage3"):
        return fail(f"unexpected Stage 3 repository version: {state.get('repository_version')!r}")
    if state.get("required_dotnet_sdk") != "10.0.400" or global_json.get("sdk", {}).get("version") != "10.0.400":
        return fail("Stage 3 must preserve the certified .NET SDK 10.0.400 checkpoint")
    if state.get("stage2_promoted") is not True:
        return fail("Stage 3 source does not record the promoted Stage 2 checkpoint")
    if state.get("stage2_manual_visual_acceptance") is not True or state.get("stage2_user_clicked_editor_retest") is not True:
        return fail("Stage 3 source does not record the user's real-PC Stage 2 visual acceptance")
    if state.get("stage3_slice1_started") is not True:
        return fail("Stage 3 Slice 1 is not recorded as started")
    if state.get("whole_application_final_claimed") is not False:
        return fail("Stage 3 source incorrectly claims the whole application is final")

    required_files = (
        "src/CloudScribe.Infrastructure/Persistence/CloudScribeDbContext.cs",
        "src/CloudScribe.Infrastructure/Persistence/LegacyDatabaseMigrationBridge.cs",
        "src/CloudScribe.Infrastructure/Persistence/Migrations/Stage2Baseline.cs",
        "src/CloudScribe.Infrastructure/Persistence/Migrations/Stage3Documents.cs",
        "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",
        "src/CloudScribe.Infrastructure/Files/DocumentContentCommit.cs",
        "tests/CloudScribe.Infrastructure.Tests/Stage3MigrationTests.cs",
        "tests/CloudScribe.Infrastructure.Tests/DocumentContentStoreTests.cs",
    )
    for relative in required_files:
        if not (root / relative).is_file():
            return fail(f"required Stage 3 Slice 1 file missing: {relative}")

    if (root / "src/CloudScribe.Infrastructure/Persistence/ObservabilityDbContext.cs").exists():
        return fail("legacy ObservabilityDbContext remains after Stage 3 context consolidation")

    try:
        initializer = require_text(
            root,
            "src/CloudScribe.Infrastructure/Persistence/DatabaseInitializer.cs",
            "MigrateAsync",
            "PRAGMA integrity_check",
            "PRAGMA foreign_key_check",
            "PRAGMA journal_mode=WAL",
            "CreateVerifiedPreMigrationBackupAsync",
            "CheckpointWalAsync",
            "RestoreBackup",
            "ClearAllPools",
        )
        if "EnsureCreatedAsync" in initializer:
            return fail("Stage 3 DatabaseInitializer still uses EnsureCreatedAsync instead of executable migrations")

        require_text(
            root,
            "src/CloudScribe.Infrastructure/Persistence/CloudScribeDbContext.cs",
            "DbSet<DocumentEntity>",
            "DbSet<DocumentRevisionEntity>",
            "DbSet<DocumentSectionEntity>",
            "DbSet<TagEntity>",
            "DbSet<BookmarkEntity>",
            "DbSet<ReadingPositionEntity>",
            "DeleteBehavior.Cascade",
            "IsConcurrencyToken",
        )
        require_text(
            root,
            "src/CloudScribe.Infrastructure/Persistence/LegacyDatabaseMigrationBridge.cs",
            "__EFMigrationsHistory",
            "Stage2Baseline.MigrationId",
            "missing expected columns",
            "unexpected tables",
            "RecoverAbandonedEfMigrationLockAsync",
            "BEGIN IMMEDIATE",
        )
        require_text(
            root,
            "src/CloudScribe.Infrastructure/Files/DocumentContentStore.cs",
            "PhysicalDirectoryPolicy",
            "FileMode.CreateNew",
            "Flush(flushToDisk: true)",
            "File.Move",
            "SHA256.HashData",
            "ValidateExistsWithoutLinks",
            "documents directory",
        )
        require_text(
            root,
            "src/CloudScribe.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs",
            "AddPooledDbContextFactory<CloudScribeDbContext>",
            "ForeignKeys = true",
            "DefaultTimeout = 5",
            "DocumentContentStore",
        )
        require_text(
            root,
            "tests/CloudScribe.Infrastructure.Tests/Stage3MigrationTests.cs",
            "FreshDatabaseAppliesExecutableStage2AndStage3Migrations",
            "Stage2EnsureCreatedShapeIsBridgedWithoutDroppingExistingRows",
            "PartialLegacySchemaFailsClosedInsteadOfGuessing",
        )
    except (OSError, ValueError) as exc:
        return fail(str(exc))

    print("PASS: Stage 3 Slice 1 source contracts are present and fail-closed migration/file-store safeguards are wired.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
