#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import os
import stat
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "SHA256SUMS.txt"
FORBIDDEN_PARTS = {".git", ".vs", "bin", "obj", "node_modules", "__pycache__"}
MAX_TREE_ENTRIES = 8192
MAX_SOURCE_FILES = 4096
MAX_FILE_BYTES = 128 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024


class ManifestError(RuntimeError):
    pass


def digest(path: Path) -> str:
    before = path.lstat()
    if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
        raise ManifestError(f"source manifest accepts only regular non-link files: {path}")
    if before.st_size > MAX_FILE_BYTES:
        raise ManifestError(f"source file exceeds {MAX_FILE_BYTES} bytes: {path}")

    value = hashlib.sha256()
    with path.open("rb") as stream:
        opened = os.fstat(stream.fileno())
        if not stat.S_ISREG(opened.st_mode) or opened.st_size != before.st_size:
            raise ManifestError(f"source file changed before hashing: {path}")
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)

    after = path.lstat()
    if (
        stat.S_ISLNK(after.st_mode)
        or after.st_size != before.st_size
        or after.st_mtime_ns != before.st_mtime_ns
    ):
        raise ManifestError(f"source file changed while hashing: {path}")
    return value.hexdigest()


def collect_files(root: Path, manifest: Path) -> list[Path]:
    root = root.absolute()
    manifest = manifest.absolute()
    entries = 0
    files: list[Path] = []
    total_bytes = 0

    for current_text, directory_names, file_names in os.walk(root, topdown=True, followlinks=False):
        current = Path(current_text)
        safe_directories: list[str] = []
        for name in sorted(directory_names):
            entries += 1
            if entries > MAX_TREE_ENTRIES:
                raise ManifestError(f"source tree contains more than {MAX_TREE_ENTRIES} entries")
            candidate = current / name
            if name in FORBIDDEN_PARTS:
                continue
            if candidate.is_symlink():
                raise ManifestError(f"symbolic-link directory is not permitted: {candidate.relative_to(root)}")
            safe_directories.append(name)
        directory_names[:] = safe_directories

        for name in sorted(file_names):
            entries += 1
            if entries > MAX_TREE_ENTRIES:
                raise ManifestError(f"source tree contains more than {MAX_TREE_ENTRIES} entries")
            candidate = current / name
            relative = candidate.relative_to(root)
            if any(part in FORBIDDEN_PARTS for part in relative.parts) or candidate.absolute() == manifest:
                continue
            metadata = candidate.lstat()
            if stat.S_ISLNK(metadata.st_mode):
                raise ManifestError(f"symbolic-link file is not permitted: {relative}")
            if not stat.S_ISREG(metadata.st_mode):
                raise ManifestError(f"non-regular source entry is not permitted: {relative}")
            if metadata.st_size > MAX_FILE_BYTES:
                raise ManifestError(f"source file exceeds {MAX_FILE_BYTES} bytes: {relative}")
            total_bytes += metadata.st_size
            if total_bytes > MAX_TOTAL_BYTES:
                raise ManifestError(f"source tree exceeds {MAX_TOTAL_BYTES} total bytes")
            files.append(candidate)
            if len(files) > MAX_SOURCE_FILES:
                raise ManifestError(f"source tree contains more than {MAX_SOURCE_FILES} files")

    return sorted(files, key=lambda item: item.relative_to(root).as_posix())


def render(root: Path = ROOT, manifest: Path = MANIFEST) -> str:
    files = collect_files(root, manifest)
    return "".join(
        f"{digest(path)}  {path.relative_to(root).as_posix()}\n"
        for path in files
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        expected = render()
    except (OSError, ManifestError) as exc:
        print(f"Source manifest FAILED: {exc}")
        return 1
    if args.check:
        actual = MANIFEST.read_text(encoding="utf-8") if MANIFEST.exists() else ""
        if actual != expected:
            print("SHA256SUMS.txt is stale; run tools/update_sha256_manifest.py after freezing the tree.")
            return 1
        print("SHA256SUMS.txt matches the current source tree.")
        return 0
    MANIFEST.write_text(expected, encoding="utf-8", newline="\n")
    print(f"Wrote {len(expected.splitlines())} SHA-256 entries.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
