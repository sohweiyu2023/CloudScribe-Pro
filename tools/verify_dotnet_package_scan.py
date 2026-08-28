#!/usr/bin/env python3
"""Fail a Stage 1/2 gate when machine-readable .NET package scans are incomplete or contain findings."""

from __future__ import annotations

import json
import pathlib
import re
import sys
from typing import Any

MAX_SCAN_FILES = 128
MAX_SCAN_BYTES = 5 * 1024 * 1024
SCAN_NAME = re.compile(r"^(?P<index>\d+)-(?P<kind>vulnerable|deprecated)\.json$")


class ScanError(ValueError):
    pass


def read_bounded_text(path: pathlib.Path) -> str:
    with path.open("rb") as stream:
        payload = stream.read(MAX_SCAN_BYTES + 1)
    if len(payload) > MAX_SCAN_BYTES:
        raise ScanError(f"{path.name}: scan JSON exceeds {MAX_SCAN_BYTES} bytes")
    return payload.decode("utf-8-sig")


def reject_duplicate_members(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ScanError(f"duplicate JSON member {key!r}")
        result[key] = value
    return result


def package_findings(value: Any, location: str = "$") -> list[str]:
    findings: list[str] = []
    if isinstance(value, dict):
        package_id = value.get("id") or value.get("name") or value.get("packageId")
        for key in ("vulnerabilities", "deprecationReasons"):
            detail = value.get(key)
            if isinstance(detail, list) and detail:
                identity = f" package {package_id!r}" if isinstance(package_id, str) else ""
                findings.append(f"{location}{identity} has non-empty {key}")
        if value.get("deprecated") is True or value.get("isDeprecated") is True:
            identity = f" package {package_id!r}" if isinstance(package_id, str) else ""
            findings.append(f"{location}{identity} is marked deprecated")
        for key, child in value.items():
            findings.extend(package_findings(child, f"{location}.{key}"))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            findings.extend(package_findings(child, f"{location}[{index}]"))
    return findings


def validate_file(path: pathlib.Path, expected_kind: str) -> list[str]:
    try:
        if path.is_symlink():
            raise ScanError(f"{path.name}: symbolic-link scan files are not accepted")
        document = json.loads(
            read_bounded_text(path),
            object_pairs_hook=reject_duplicate_members,
        )
    except (OSError, UnicodeError, json.JSONDecodeError, ScanError) as exc:
        raise ScanError(f"{path.name}: invalid scan JSON: {exc}") from exc
    if not isinstance(document, dict):
        raise ScanError(f"{path.name}: top-level scan JSON must be an object")
    if document.get("version") != 1:
        raise ScanError(f"{path.name}: scan output version must be exactly 1")
    parameters = document.get("parameters")
    if not isinstance(parameters, str) or f"--{expected_kind}" not in parameters.split():
        raise ScanError(f"{path.name}: parameters do not prove a --{expected_kind} scan")
    projects = document.get("projects")
    if not isinstance(projects, list) or not projects:
        raise ScanError(f"{path.name}: scan JSON must contain at least one project")
    return package_findings(document)


def discover_scan_files(directory: pathlib.Path) -> list[tuple[pathlib.Path, str]]:
    if directory.is_symlink():
        raise ScanError("scan directory must not be a symbolic link")
    if not directory.is_dir():
        raise ScanError("scan directory does not exist")
    entries: list[pathlib.Path] = []
    for path in directory.iterdir():
        if len(entries) >= MAX_SCAN_FILES:
            raise ScanError(f"scan directory contains more than {MAX_SCAN_FILES} entries")
        if path.is_symlink():
            raise ScanError("scan directory contains a symbolic-link entry")
        if not path.is_file():
            raise ScanError(f"unexpected non-file scan entry {path.name!r}")
        entries.append(path)
    candidates = sorted(entries)
    if not candidates:
        raise ScanError("no scan files were produced")
    parsed: list[tuple[pathlib.Path, str]] = []
    seen: dict[int, set[str]] = {}
    for path in candidates:
        match = SCAN_NAME.fullmatch(path.name)
        if match is None:
            raise ScanError(f"unexpected scan file name {path.name!r}")
        index = int(match.group("index"))
        kind = match.group("kind")
        if kind in seen.setdefault(index, set()):
            raise ScanError(f"duplicate {kind} scan for project index {index}")
        seen[index].add(kind)
        parsed.append((path, kind))

    expected_indexes = list(range(len(seen)))
    if sorted(seen) != expected_indexes:
        raise ScanError("project scan indexes must be contiguous from zero")
    for index, kinds in seen.items():
        if kinds != {"vulnerable", "deprecated"}:
            raise ScanError(f"project index {index} does not have both vulnerable and deprecated scans")
    return parsed


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify_dotnet_package_scan.py <scan-directory>", file=sys.stderr)
        return 2

    findings: list[str] = []
    try:
        raw_directory = pathlib.Path(sys.argv[1])
        if raw_directory.is_symlink():
            raise ScanError("scan directory must not be a symbolic link")
        scans = discover_scan_files(raw_directory.resolve())
        for path, kind in scans:
            findings.extend(f"{path.name}: {item}" for item in validate_file(path, kind))
    except ScanError as exc:
        print(f"Package scan validation FAILED: {exc}", file=sys.stderr)
        return 1

    if findings:
        print("Package scan validation FAILED: vulnerability/deprecation findings are present", file=sys.stderr)
        for finding in findings:
            print(f" - {finding}", file=sys.stderr)
        return 1

    print(f"Package scan validation PASSED ({len(scans)} paired machine-readable scan files, no findings)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
