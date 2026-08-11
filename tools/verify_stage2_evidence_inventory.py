#!/usr/bin/env python3
from __future__ import annotations

import json
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify_stage2_evidence_inventory.py EVIDENCE_ROOT", file=sys.stderr)
        return 2
    root = Path(sys.argv[1]).resolve()
    if not root.is_dir():
        return fail(f"evidence root is not a directory: {root}")
    if root.is_symlink() or (hasattr(root, "is_junction") and root.is_junction()):
        return fail("evidence root must be a physical directory")

    for path in root.rglob("*"):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            return fail(f"evidence contains a link/junction: {path.relative_to(root)}")

    scans = sorted((root / "package-scans").rglob("*.json")) if (root / "package-scans").is_dir() else []
    if len(scans) != 18:
        return fail(f"expected exactly 18 package-scan JSON files, found {len(scans)}")
    for path in scans:
        try:
            payload = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError) as exc:
            return fail(f"invalid package-scan JSON {path.name}: {exc}")
        if not isinstance(payload, (dict, list)):
            return fail(f"unexpected package-scan JSON shape: {path.name}")

    visual = root / "visual"
    pngs = sorted(visual.glob("*.png")) if visual.is_dir() else []
    if len(pngs) != 17:
        return fail(f"expected exactly 17 visual PNG files, found {len(pngs)}")
    dimensions: set[tuple[int, int]] = set()
    for path in pngs:
        data = path.read_bytes()
        if len(data) < 32 or not data.startswith(PNG_SIGNATURE):
            return fail(f"invalid/empty PNG evidence: {path.name}")
        width, height = struct.unpack(">II", data[16:24])
        if width < 500 or height < 700:
            return fail(f"implausible capture dimensions for {path.name}: {width}x{height}")
        dimensions.add((width, height))
    if len(dimensions) < 5:
        return fail(f"visual evidence does not span enough distinct viewport dimensions: {sorted(dimensions)}")

    manifests = sorted(visual.glob("*.json"))
    case_counts: list[int] = []
    for path in manifests:
        try:
            payload = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(payload, dict):
            cases = payload.get("Cases", payload.get("cases"))
            if isinstance(cases, list):
                case_counts.append(len(cases))
    if 17 not in case_counts:
        return fail("visual evidence is missing a manifest containing exactly 17 cases")

    trx_files = sorted((root / "test-results").rglob("*.trx")) if (root / "test-results").is_dir() else []
    if len(trx_files) != 4:
        return fail(f"expected exactly 4 TRX test-result files, found {len(trx_files)}")
    total = failed = skipped = 0
    for path in trx_files:
        try:
            tree = ET.parse(path)
        except ET.ParseError as exc:
            return fail(f"invalid TRX XML {path}: {exc}")
        counters = next((node for node in tree.getroot().iter() if node.tag.rsplit("}", 1)[-1] == "Counters"), None)
        if counters is None:
            return fail(f"TRX counters missing: {path}")
        total += int(counters.attrib.get("total", "0"))
        failed += int(counters.attrib.get("failed", "0"))
        skipped += int(counters.attrib.get("skipped", "0") or "0")
    if (total, failed, skipped) != (147, 0, 0):
        return fail(f"unexpected .NET test evidence totals: total={total} failed={failed} skipped={skipped}")

    ledger_path = root / "logs" / "command-ledger.jsonl"
    if not ledger_path.is_file():
        return fail("command ledger is missing")
    records = []
    for line_number, line in enumerate(ledger_path.read_text(encoding="utf-8-sig").splitlines(), 1):
        if not line.strip():
            continue
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError as exc:
            return fail(f"invalid command ledger JSON at line {line_number}: {exc}")
    if not records:
        return fail("command ledger is empty")
    sequences = [int(item.get("sequence", -1)) for item in records]
    if sequences != list(range(1, len(records) + 1)):
        return fail(f"command ledger sequence is not contiguous from 1: {sequences[:5]}...{sequences[-5:]}")
    if any(item.get("status") != "passed" for item in records):
        return fail("command ledger contains a non-passing step before evidence inventory validation")

    print(
        "PASS: exact Stage 2 evidence inventory verified: "
        f"18 package scans, 17 PNG captures, 4 TRX files / 147 tests, {len(records)} completed ledger steps."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
