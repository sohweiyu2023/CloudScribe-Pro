#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

SHA_LINE = re.compile(r"^(?P<sha>[0-9a-fA-F]{64})\s+\*(?P<name>[^\r\n]+)$")


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def canonical_zip_name(name: str) -> str:
    if "\\" in name:
        raise ValueError("backslash path separators are forbidden")
    pure = PurePosixPath(name)
    if pure.is_absolute() or not pure.parts or any(part in ("", ".", "..") for part in pure.parts):
        raise ValueError("absolute, empty, dot and traversal path components are forbidden")
    if any(part.endswith(" ") or part.endswith(".") for part in pure.parts):
        raise ValueError("Windows-ambiguous trailing space/dot path components are forbidden")
    return "/".join(part.casefold() for part in pure.parts)


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify a CloudScribe source ZIP, clean extraction, regression inventory and post-test manifest immutability.")
    parser.add_argument("archive")
    args = parser.parse_args()
    archive = Path(args.archive).resolve()
    if not archive.is_file() or archive.suffix.lower() != ".zip":
        return fail(f"source archive is missing/not a ZIP: {archive}")

    checksum = Path(str(archive) + ".sha256")
    if not checksum.is_file():
        return fail(f"sibling SHA-256 file missing: {checksum.name}")
    match = SHA_LINE.fullmatch(checksum.read_text(encoding="utf-8-sig").strip())
    if not match or match.group("name") != archive.name:
        return fail("checksum file format/name binding is invalid")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if digest.lower() != match.group("sha").lower():
        return fail(f"archive SHA-256 mismatch: expected={match.group('sha')} actual={digest}")

    with zipfile.ZipFile(archive, "r") as zf:
        names = zf.namelist()
        if not names or len(names) != len(set(names)):
            return fail("ZIP is empty or contains duplicate entry names")
        roots: set[str] = set()
        canonical_names: set[str] = set()
        for info in zf.infolist():
            try:
                canonical_name = canonical_zip_name(info.filename.rstrip("/"))
            except ValueError as exc:
                return fail(f"unsafe ZIP path {info.filename!r}: {exc}")
            if canonical_name in canonical_names:
                return fail(f"ZIP contains a Windows-equivalent duplicate/colliding path: {info.filename!r}")
            canonical_names.add(canonical_name)

            pure = PurePosixPath(info.filename)
            roots.add(pure.parts[0])
            mode = (info.external_attr >> 16) & 0o170000
            if mode == 0o120000:
                return fail(f"ZIP contains symbolic-link entry: {info.filename}")
        if len(roots) != 1:
            return fail(f"ZIP must contain exactly one release root, found {sorted(roots)}")
        release_root_name = next(iter(roots))
        if release_root_name != archive.stem:
            return fail(f"ZIP root must equal archive stem: root={release_root_name!r} stem={archive.stem!r}")

        with tempfile.TemporaryDirectory(prefix="cloudscribe-source-release-") as temp:
            temp_root = Path(temp).resolve()
            for info in zf.infolist():
                destination = (temp_root / PurePosixPath(info.filename)).resolve()
                try:
                    destination.relative_to(temp_root)
                except ValueError:
                    return fail(f"ZIP entry escapes extraction root: {info.filename}")
                if info.is_dir():
                    destination.mkdir(parents=True, exist_ok=True)
                    continue
                destination.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(info, "r") as source, destination.open("xb") as target:
                    shutil.copyfileobj(source, target)

            root = temp_root / release_root_name
            manifest_before = hashlib.sha256((root / "SHA256SUMS.txt").read_bytes()).hexdigest()
            try:
                state = __import__("json").loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
            except (OSError, ValueError) as exc:
                return fail(f"fresh-extracted SESSION_STATE.json is invalid: {exc}")
            stage = state.get("current_stage")
            checks = [
                [sys.executable, "-B", "tools/run_verifier_self_tests.py"],
                [sys.executable, "tools/update_sha256_manifest.py", "--check"],
                [sys.executable, "tools/verify_repository.py"],
                [sys.executable, "tools/verify_project_dependencies.py"],
                [sys.executable, "tools/verify_stage1_source.py"],
            ]
            if stage == 2:
                checks.extend([
                    [sys.executable, "tools/verify_stage2_source.py"],
                    [sys.executable, "tools/run_python_regression_shards.py", "--all"],
                ])
            elif stage == 3:
                checks.append([sys.executable, "tools/verify_stage3_source.py"])
            else:
                return fail(f"unsupported fresh-extracted stage for source release verification: {stage!r}")
            for command in checks:
                result = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=900, check=False)
                if result.returncode != 0:
                    return fail(f"fresh-extracted release verification failed: {' '.join(command)}\n{result.stdout[-6000:]}")
            manifest_after = hashlib.sha256((root / "SHA256SUMS.txt").read_bytes()).hexdigest()
            if manifest_before != manifest_after:
                return fail("SHA256SUMS.txt changed during fresh-extracted release verification")

    print(f"PASS: source release checksum, safe clean extraction, source contracts and regression inventory verified: {archive.name} sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
