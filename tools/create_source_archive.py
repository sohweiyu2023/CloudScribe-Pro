#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import zipfile
from pathlib import Path

EXCLUDED_SEGMENTS = {"bin", "obj", "TestResults", "__pycache__", ".vs", ".git"}
FIXED_TIME = (2026, 8, 10, 0, 0, 0)


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def releasable(root: Path) -> list[Path]:
    files = []
    for path in root.rglob("*"):
        rel = path.relative_to(root)
        if EXCLUDED_SEGMENTS.intersection(rel.parts):
            continue
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError(f"release source contains link/junction: {rel.as_posix()}")
        if path.is_file():
            files.append(path)
    return sorted(files, key=lambda p: p.relative_to(root).as_posix())


def main() -> int:
    parser = argparse.ArgumentParser(description="Create a deterministic, no-overwrite CloudScribe source ZIP bound to SESSION_STATE.json.")
    parser.add_argument("--output-directory", default=".")
    parser.add_argument("--name", help="archive stem; defaults to CloudScribe_Pro_Source_<repository_version>")
    args = parser.parse_args()

    root = Path.cwd().resolve()
    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"cannot read SESSION_STATE.json: {exc}")
    version = str(state.get("repository_version", "")).strip()
    if not (version.startswith("0.3.48-") or version.startswith("0.4.0-stage3")):
        return fail(f"refusing archive for unexpected repository version: {version!r}")
    expected_name = f"CloudScribe_Pro_Source_{version}"
    name = args.name or expected_name
    if name != expected_name:
        return fail(f"archive name must match source identity exactly: expected {expected_name!r}, got {name!r}")

    manifest = subprocess.run(
        [sys.executable, "tools/update_sha256_manifest.py", "--check"],
        cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120, check=False,
    )
    if manifest.returncode != 0:
        return fail("source SHA-256 manifest is not clean before archive creation:\n" + manifest.stdout[-4000:])

    output_dir = Path(args.output_directory).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    archive = output_dir / f"{name}.zip"
    checksum = output_dir / f"{name}.zip.sha256"
    if archive.exists() or checksum.exists():
        return fail(f"no-overwrite publication refused because output already exists: {archive}")

    try:
        files = releasable(root)
    except ValueError as exc:
        return fail(str(exc))
    if len(files) < 140:
        return fail(f"unexpectedly small release inventory: {len(files)} files")

    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for path in files:
            rel = path.relative_to(root).as_posix()
            info = zipfile.ZipInfo(f"{name}/{rel}", FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            zf.writestr(info, path.read_bytes())

    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    checksum.write_text(f"{digest} *{archive.name}\n", encoding="utf-8", newline="\n")
    print(f"PASS: deterministic source archive created: {archive} ({len(files)} files) sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
