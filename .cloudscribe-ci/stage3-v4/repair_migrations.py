from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import runpy
import subprocess


def run_git(source_root: Path, *args: str) -> list[str]:
    result = subprocess.run(
        ['git', '-C', str(source_root), *args],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        raise SystemExit(f"Git command failed ({' '.join(args)}): {detail}")
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def current_change_sets(source_root: Path) -> tuple[set[str], set[str]]:
    tracked = set(run_git(source_root, 'diff', '--name-only', '--no-renames', '--'))
    untracked = set(run_git(source_root, 'ls-files', '--others', '--exclude-standard'))
    return tracked, untracked


def hash_files(source_root: Path, paths: set[str]) -> dict[str, str]:
    hashes: dict[str, str] = {}
    for path in sorted(paths):
        full = source_root / path
        if full.is_file():
            hashes[path] = hashlib.sha256(full.read_bytes()).hexdigest()
    return hashes


def restore_tracked_collateral(source_root: Path, paths: set[str]) -> None:
    ordered = sorted(paths)
    for index in range(0, len(ordered), 40):
        batch = ordered[index:index + 40]
        result = subprocess.run(
            ['git', '-C', str(source_root), 'restore', '--worktree', '--source=HEAD', '--', *batch],
            check=False,
        )
        if result.returncode != 0:
            raise SystemExit('Unable to restore formatter collateral outside Stage 3 scope')


def remove_untracked_collateral(source_root: Path, paths: set[str]) -> None:
    for path in sorted(paths):
        full = source_root / path
        if full.is_file() or full.is_symlink():
            full.unlink()
        elif full.exists():
            raise SystemExit(f'Unexpected untracked directory outside Stage 3 scope: {path}')


def format_stage3_scope(
    source_root: Path,
    *,
    verify_unchanged: bool,
    allowed_paths: set[str] | None = None,
) -> None:
    tracked_before, untracked_before = current_change_sets(source_root)
    if allowed_paths is None:
        allowed = tracked_before | untracked_before
    else:
        allowed = set(allowed_paths)

    unexpected_tracked_before = tracked_before - allowed
    if unexpected_tracked_before:
        raise SystemExit(
            'Tracked files changed outside frozen Stage 3 scope before formatting: '
            + ', '.join(sorted(unexpected_tracked_before))
        )

    changed_cs = {
        path
        for path in allowed
        if path.lower().endswith('.cs') and (source_root / path).is_file()
    }
    if not changed_cs:
        raise SystemExit('No changed C# files found for Stage 3 formatting')

    before_hashes = hash_files(source_root, changed_cs) if verify_unchanged else {}
    print(
        'CLOUDSCRIBE_STAGE3_FORMAT_SCOPE '
        f'allowed_paths={len(allowed)} changed_cs={len(changed_cs)} '
        f'preexisting_untracked_collateral={len(untracked_before - allowed)} '
        f'verify_unchanged={str(verify_unchanged).lower()}'
    )

    # dotnet format on this Windows runner can rewrite pre-existing baseline files
    # even when --include is supplied. Let it inspect the solution, then discard all
    # effects outside the exact Stage 3 scope frozen before restore/build collateral.
    format_result = subprocess.run(
        [
            'dotnet',
            'format',
            'CloudScribe.sln',
            '--no-restore',
            '--verbosity',
            'minimal',
        ],
        cwd=source_root,
        check=False,
    )
    if format_result.returncode != 0:
        raise SystemExit('dotnet format failed')

    tracked_after, untracked_after = current_change_sets(source_root)
    tracked_collateral = tracked_after - allowed
    untracked_collateral = untracked_after - allowed
    if tracked_collateral:
        restore_tracked_collateral(source_root, tracked_collateral)
    if untracked_collateral:
        remove_untracked_collateral(source_root, untracked_collateral)

    tracked_final, untracked_final = current_change_sets(source_root)
    final_paths = tracked_final | untracked_final
    outside_scope = final_paths - allowed
    missing_scope = allowed - final_paths
    if outside_scope:
        raise SystemExit(
            'Formatting cleanup left changes outside Stage 3 scope: '
            + ', '.join(sorted(outside_scope))
        )
    if missing_scope:
        raise SystemExit(
            'Formatting cleanup unexpectedly removed Stage 3 changes: '
            + ', '.join(sorted(missing_scope))
        )

    if verify_unchanged:
        after_hashes = hash_files(source_root, changed_cs)
        changed = [
            path
            for path in sorted(changed_cs)
            if before_hashes.get(path) != after_hashes.get(path)
        ]
        if changed:
            raise SystemExit(
                'Stage 3 C# files were not format-stable: ' + ', '.join(changed)
            )

    print(
        'CLOUDSCRIBE_STAGE3_FORMAT_SCOPE=PASS '
        f'restored_tracked={len(tracked_collateral)} '
        f'removed_untracked={len(untracked_collateral)} '
        f'final_paths={len(final_paths)}'
    )


def repair_candidate() -> None:
    root = Path('source/src/CloudScribe.Infrastructure/Persistence/Migrations')
    p = root / '20260812122000_Stage2Baseline.cs'
    q = root / 'Stage2Baseline.cs'
    t = p.read_text(encoding='utf-8')
    a = '    public const string MigrationId = "20260812122000_Stage2Baseline";\n'
    if t.count(a) != 1:
        raise SystemExit('Stage2 anchor mismatch')
    t = t.replace(
        a,
        a + '\n    private static readonly string[] BillableOperationTimeIndexColumns = ["OperationId", "OccurredAtUnixMilliseconds"];\n',
    )
    old = 'columns: new[] { "OperationId", "OccurredAtUnixMilliseconds" }'
    if t.count(old) != 1:
        raise SystemExit('Stage2 index mismatch')
    q.write_text(t.replace(old, 'columns: BillableOperationTimeIndexColumns'), encoding='utf-8')
    p.unlink()

    p = root / '20260812123000_Stage3Documents.cs'
    q = root / 'Stage3Documents.cs'
    src = p.read_text(encoding='utf-8')
    a = '    public const string MigrationId = "20260812123000_Stage3Documents";\n'
    arrays = (
        '\n    private static readonly string[] BookmarkDocumentOffsetColumns = ["DocumentId", "GraphemeOffset"];\n'
        '    private static readonly string[] RevisionDocumentCreatedColumns = ["DocumentId", "CreatedAtUnixMilliseconds"];\n'
        '    private static readonly string[] SectionDocumentOrdinalColumns = ["DocumentId", "Ordinal"];\n'
        '    private static readonly string[] DocumentStatusUpdatedColumns = ["Status", "UpdatedAtUnixMilliseconds"];\n'
    )
    if src.count(a) != 1:
        raise SystemExit('Stage3 anchor mismatch')
    header = src[:src.index('    protected override void Up')].replace(a, a + arrays)
    down_start = src.index('    protected override void Down')
    down = src[down_start:]
    down_body, _ = down.rsplit('\n}', 1)
    start = src.index('        migrationBuilder.CreateTable(', src.index('    protected override void Up'))
    idx = src.index('        migrationBuilder.CreateIndex(', start)
    up_close = src.index('\n    }\n\n    protected override void Down', idx)
    region = src[start:idx]
    indexes = src[idx:up_close]
    tables = [
        'documents',
        'tags',
        'bookmarks',
        'document_revisions',
        'document_sections',
        'reading_positions',
        'document_tags',
    ]
    blocks = []
    for i, name in enumerate(tables):
        marker = f'        migrationBuilder.CreateTable(\n            name: "{name}",'
        s = region.index(marker)
        if i + 1 < len(tables):
            e = region.index(
                f'        migrationBuilder.CreateTable(\n            name: "{tables[i + 1]}",',
                s,
            )
        else:
            e = len(region)
        blocks.append(region[s:e].rstrip() + '\n')
    for old, new in (
        ('columns: new[] { "DocumentId", "GraphemeOffset" }', 'columns: BookmarkDocumentOffsetColumns'),
        ('columns: new[] { "DocumentId", "CreatedAtUnixMilliseconds" }', 'columns: RevisionDocumentCreatedColumns'),
        ('columns: new[] { "DocumentId", "Ordinal" }', 'columns: SectionDocumentOrdinalColumns'),
        ('columns: new[] { "Status", "UpdatedAtUnixMilliseconds" }', 'columns: DocumentStatusUpdatedColumns'),
    ):
        indexes = indexes.replace(old, new)
    if 'new[] {' in indexes:
        raise SystemExit('unhoisted Stage3 index array remains')
    names = [
        'CreateDocuments',
        'CreateTags',
        'CreateBookmarks',
        'CreateRevisions',
        'CreateSections',
        'CreateReadingPositions',
        'CreateDocumentTags',
    ]
    up = (
        '    protected override void Up(MigrationBuilder migrationBuilder)\n    {\n'
        + ''.join(f'        {n}(migrationBuilder);\n' for n in names)
        + '        CreateIndexes(migrationBuilder);\n    }\n\n'
    )
    helpers = ''.join(
        f'    private static void {n}(MigrationBuilder migrationBuilder)\n    {{\n{b}    }}\n\n'
        for n, b in zip(names, blocks, strict=True)
    )
    helpers += (
        '    private static void CreateIndexes(MigrationBuilder migrationBuilder)\n'
        f'    {{\n{indexes.rstrip()}\n    }}\n'
    )
    q.write_text(header + up + down_body + '\n\n' + helpers + '}\n', encoding='utf-8')
    p.unlink()
    print('CLOUDSCRIBE_STAGE3_MIGRATION_REPAIR=PASS')

    runpy.run_path(
        str(Path(__file__).with_name('repair_bridge_analyzers.py')),
        run_name='__main__',
    )

    source_root = Path('source')
    tracked_scope, untracked_scope = current_change_sets(source_root)
    admitted_scope = tracked_scope | untracked_scope
    print(f'CLOUDSCRIBE_STAGE3_PREFORMAT_SCOPE_FROZEN paths={len(admitted_scope)}')

    restore = subprocess.run(
        [
            'dotnet',
            'restore',
            'CloudScribe.sln',
            '--locked-mode',
            '--disable-parallel',
            '--configfile',
            'NuGet.config',
        ],
        cwd=source_root,
        check=False,
    )
    if restore.returncode != 0:
        raise SystemExit('Pre-freeze locked restore failed')

    format_stage3_scope(
        source_root,
        verify_unchanged=False,
        allowed_paths=admitted_scope,
    )
    print('CLOUDSCRIBE_STAGE3_CANDIDATE_FORMAT_NORMALIZED=PASS')


def load_allowed_file(path: Path) -> set[str]:
    if not path.is_file():
        raise SystemExit(f'Frozen Stage 3 scope file not found: {path}')
    paths = {
        line.strip().lstrip('\ufeff')
        for line in path.read_text(encoding='utf-8-sig').splitlines()
        if line.strip().lstrip('\ufeff')
    }
    if not paths:
        raise SystemExit('Frozen Stage 3 scope file is empty')
    return paths


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--format-only', action='store_true')
    parser.add_argument('--source', default='source')
    parser.add_argument('--verify-unchanged', action='store_true')
    parser.add_argument('--allowed-file')
    args = parser.parse_args()

    if args.format_only:
        allowed = load_allowed_file(Path(args.allowed_file)) if args.allowed_file else None
        format_stage3_scope(
            Path(args.source),
            verify_unchanged=args.verify_unchanged,
            allowed_paths=allowed,
        )
        return

    repair_candidate()


if __name__ == '__main__':
    main()
