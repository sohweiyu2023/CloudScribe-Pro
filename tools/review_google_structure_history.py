"""Offline, content-free review of retained structured Google transport events.

No provider access, profile mutation, automatic tuning, or causal/optimal-length claim.
Reads bounded JSONL; ignores free-form Message/exception fields entirely.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import os
import stat
from pathlib import Path
import re
import uuid

HEX = re.compile(r"[a-fA-F0-9]{64}\Z")
VOICE = re.compile(r"[a-z]{2,3}-[A-Z]{2}-[A-Za-z0-9-]{1,60}\Z")
CORRELATION = re.compile(r"[a-fA-F0-9]{32}\Z")
ENUM = re.compile(r"[A-Za-z][A-Za-z0-9]{0,63}\Z")
NUMERIC = ("InputUtf8Bytes", "SourceScalars", "Sentences", "MaximumSentenceScalars",
           "MaximumSentenceUtf8Bytes", "MedianSentenceScalars", "P95SentenceScalars", "SplitDepth")


def safe_number(value):
    try:
        result = int(value)
        return result if -1 <= result <= 16_777_216 else None
    except (ValueError, TypeError):
        return None


def safe_token(value, pattern=ENUM):
    return value if isinstance(value, str) and pattern.fullmatch(value) else None


def safe_timestamp(event):
    stamp = event.get("TimestampUnixMilliseconds")
    if type(stamp) is int and stamp >= 0:
        return stamp
    value = event.get("TimestampUtc")
    if not isinstance(value, str) or len(value) > 64:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            return None
        stamp = int(parsed.timestamp() * 1000)
        return stamp if stamp >= 0 else None
    except (ValueError, OverflowError, OSError):
        return None


def reverse_lines(stream, start, end, max_line_bytes, coverage):
    """Yield newest JSONL records using bounded blocks and a bounded line buffer."""
    stream.seek(start - 1 if start else 0)
    preceding = stream.read(1) if start else b""
    partial_first = bool(start and preceding not in (b"\n", b"\r"))
    if preceding == b"\r" and stream.read(1) == b"\n":
        start += 1  # A budget boundary inside CRLF must not create an empty record.
    position, fragments, pending_size, oversized = end, [], 0, False
    trailing = True
    while position > start:
        amount = min(65_536, position - start)
        position -= amount
        stream.seek(position)
        block = stream.read(amount)
        if len(block) != amount:
            raise ValueError("Log changed during bounded snapshot; retry after a safe checkpoint")
        if position > start and block.startswith(b"\n"):
            stream.seek(position - 1)
            if stream.read(1) == b"\r":
                position -= 1
                block = b"\r" + block
        parts = re.split(b"\r\n|\r|\n", block)
        for index in range(len(parts) - 1, -1, -1):
            fragment = parts[index]
            if not oversized:
                if len(fragment) + pending_size > max_line_bytes:
                    fragments, pending_size, oversized = [], 0, True
                else:
                    fragments.append(fragment)
                    pending_size += len(fragment)
            if index:
                if trailing and not pending_size and not oversized:
                    pass  # splitlines does not emit an empty row after the final newline.
                elif oversized:
                    coverage["OversizedLines"] += 1
                else:
                    yield b"".join(reversed(fragments))
                trailing = False
                fragments, pending_size, oversized = [], 0, False
    if partial_first:
        coverage["PartialBoundaryLines"] += 1
    elif oversized:
        coverage["OversizedLines"] += 1
    elif pending_size or (end > start and not trailing):
        yield b"".join(reversed(fragments))


def snapshot_lines(path, start, expected, max_line_bytes, coverage):
    """Reject changed/non-regular sources without exposing paths or exception text."""
    # Windows Python may expose creation time via lstat ctime but change time via
    # fstat ctime. Compare shared fields across APIs and each API's full snapshot
    # against its own baseline, retaining native ctime mutation detection.
    shared_identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_mode)
    identity = lambda s: (*shared_identity(s), s.st_ctime_ns)
    try:
        flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
        with os.fdopen(os.open(path, flags), "rb") as stream:
            opened = os.fstat(stream.fileno())
            if not stat.S_ISREG(opened.st_mode) or shared_identity(opened) != shared_identity(expected):
                raise ValueError("Log changed during bounded snapshot; retry after a safe checkpoint")
            yield from reverse_lines(stream, start, expected.st_size, max_line_bytes, coverage)
            after = path.lstat()
            if (identity(os.fstat(stream.fileno())) != identity(opened)
                    or identity(after) != identity(expected) or stat.S_ISLNK(after.st_mode)):
                raise ValueError("Log changed during bounded snapshot; retry after a safe checkpoint")
    except OSError:
        raise ValueError("Log could not be read safely; retry after a safe checkpoint") from None


def review(directory: Path, max_files=256, max_bytes=33_554_432, max_line_bytes=1_048_576):
    if any(type(x) is not int or x < 1 for x in (max_files, max_bytes, max_line_bytes)):
        raise ValueError("Scan limits must be positive integers")
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("A physical existing log directory is required")
    try:
        all_files = sorted(directory.glob("*.jsonl"), key=lambda p: p.lstat().st_mtime_ns, reverse=True)
    except OSError:
        raise ValueError("Log inventory could not be read safely") from None
    requests, identities = {}, {}
    read_bytes = bad_lines = skipped_files = incomplete_events = 0
    first_stamp = last_stamp = None
    coverage = {"OversizedLines": 0, "PartialBoundaryLines": 0, "PartiallyScannedFiles": 0,
                "OmittedBytes": 0, "ScanByteLimit": max_bytes, "LineByteLimit": max_line_bytes}
    scanned = 0
    for path in all_files[:max_files]:
        try:
            metadata = path.lstat()
        except OSError:
            raise ValueError("Log inventory changed; retry after a safe checkpoint") from None
        size = metadata.st_size
        if not stat.S_ISREG(metadata.st_mode) or stat.S_ISLNK(metadata.st_mode) or read_bytes >= max_bytes:
            skipped_files += 1
            if stat.S_ISREG(metadata.st_mode):
                coverage["OmittedBytes"] += size
            continue
        start = max(0, size - (max_bytes - read_bytes))
        if start:
            coverage["PartiallyScannedFiles"] += 1
            coverage["OmittedBytes"] += start
        read_bytes += size - start
        scanned += 1
        for line in snapshot_lines(path, start, metadata, max_line_bytes, coverage):
            try:
                event = json.loads(line)
                if not isinstance(event, dict):
                    bad_lines += 1
                    continue
                fields = event.get("SafeFields", {})
                if not isinstance(fields, dict):
                    bad_lines += 1
                    continue
                if event.get("FieldsComplete") is False:
                    incomplete_events += 1
                stamp = safe_timestamp(event)
                if isinstance(stamp, int) and stamp >= 0:
                    first_stamp = stamp if first_stamp is None else min(first_stamp, stamp)
                    last_stamp = stamp if last_stamp is None else max(last_stamp, stamp)
                eid = event.get("EventId")
                key = safe_token(fields.get("IdempotencyKeySha256"), HEX)
                if eid in (1613, 1617) and key:
                    identity = identities.setdefault(key, {"Ordinal": safe_number(fields.get("Ordinal"))})
                    if eid == 1617:
                        depth = safe_number(fields.get("SplitDepth"))
                        if depth is not None and depth >= 0:
                            identity.setdefault("SplitDepth", depth)
                        try:
                            identity.setdefault("AuthorizationId", str(uuid.UUID(fields.get("AuthorizationId"))))
                        except (ValueError, TypeError, AttributeError):
                            pass
                correlation = safe_token(fields.get("Correlation"), CORRELATION)
                if not correlation or eid not in (1603, 1607, 1608, 1609, 1611, 1614, 1616):
                    continue
                item = requests.setdefault(correlation, {"Correlation": correlation})
                version = event.get("ApplicationVersion")
                if isinstance(version, str) and re.fullmatch(r"[0-9]{1,4}(?:\.[0-9]{1,4}){1,3}", version):
                    item.setdefault("ApplicationVersion", version)
                module = event.get("ComponentBuildId") or event.get("ModuleId")
                try:
                    if isinstance(module, str):
                        item.setdefault("ModuleId", str(uuid.UUID(module)))
                except ValueError:
                    pass
                if isinstance(stamp, int):
                    item["FirstObservedUnixMilliseconds"] = min(stamp, item.get("FirstObservedUnixMilliseconds", stamp))
                    item["LastObservedUnixMilliseconds"] = max(stamp, item.get("LastObservedUnixMilliseconds", stamp))
                if key:
                    item.setdefault("RequestKeySha256", key)
                payload = safe_token(fields.get("PayloadSha256"), HEX)
                if payload:
                    item.setdefault("PayloadSha256", payload)
                if eid == 1616:
                    item.setdefault("StructureVersion", 1)
                    item.setdefault("VoiceName", safe_token(fields.get("VoiceName"), VOICE))
                    item.setdefault("InputMode", safe_token(fields.get("InputMode")))
                    for name in NUMERIC:
                        item.setdefault(name, safe_number(fields.get(name)))
                    histogram = fields.get("Histogram", "")
                    if isinstance(histogram, str) and re.fullmatch(r"-?\d+(?:,-?\d+){4}", histogram):
                        item.setdefault("Histogram", [safe_number(x) for x in histogram.split(",")])
                    item.setdefault("StructureComplete", fields.get("Complete") == "true")
                    item.setdefault("ExplicitSentenceMarkers", fields.get("ExplicitSentenceMarkers") == "true")
                elif eid == 1611:
                    item.setdefault("HttpStatus", safe_number(fields.get("HttpStatus")))
                    item.setdefault("ElapsedMilliseconds", safe_number(fields.get("ElapsedMilliseconds")))
                elif eid == 1607:
                    item.setdefault("ErrorHint", safe_token(fields.get("MessageHint")))
                elif eid in (1608, 1609):
                    item.setdefault("CaptureStatus", safe_token(fields.get("Retention")))
                    if eid == 1609:
                        item.setdefault("PrivateTruncated", fields.get("Truncated") == "true")
                elif eid == 1603:
                    item.setdefault("ErrorStatus", safe_token(fields.get("ErrorStatus")))
            except (ValueError, TypeError, RecursionError):
                bad_lines += 1
    rows = []
    for item in requests.values():
        item.update(identities.get(item.get("RequestKeySha256"), {}))
        rows.append(item)
    groups = Counter((r.get("VoiceName"), r.get("InputMode"), r.get("ApplicationVersion"), r.get("ModuleId"), r.get("MaximumSentenceScalars"),
                      r.get("HttpStatus"), r.get("ErrorHint")) for r in rows if r.get("StructureComplete"))
    return {"SchemaVersion": 1, "GeneratedUtc": datetime.now(timezone.utc).isoformat(),
            "OfflineOnly": True, "ProviderCalls": 0, "AutomaticTuning": False,
            "Coverage": {**coverage, "ScannedFiles": scanned, "OmittedFiles": max(0, len(all_files) - max_files) + skipped_files,
                         "ReadBytes": read_bytes, "MalformedLines": bad_lines, "IncompleteEvents": incomplete_events,
                         "ObservedFirstUnixMilliseconds": first_stamp,
                         "ObservedLastUnixMilliseconds": last_stamp,
                         "RequestsWithoutCompleteStructure": sum(not x.get("StructureComplete") for x in rows),
                         "RequestsWithoutTimeline": sum("FirstObservedUnixMilliseconds" not in x for x in rows),
                         "RequestsWithoutBuildIdentity": sum("ModuleId" not in x for x in rows),
                         "SourceRetention": "Content-free trend history has no automatic age/count rotation; routine logs may rotate. This bounded scan reads newest file tails; file-count/total-byte and per-line limits can omit sources or records (see coverage). Separate protected response text is not read."},
            "Caution": "Descriptive sample only. Voice/model/code/input-mode changes confound comparisons. No universal optimum or causal conclusion.",
            "Groups": [{"VoiceName": k[0], "InputMode": k[1], "ApplicationVersion": k[2], "ModuleId": k[3], "MaximumSentenceScalars": k[4],
                        "HttpStatus": k[5], "ErrorHint": k[6], "Count": n} for k, n in groups.items()],
            "Requests": sorted(rows, key=lambda x: x.get("FirstObservedUnixMilliseconds", 0))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--logs", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = review(args.logs)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=True, indent=2)
    print(json.dumps({"Written": True, "Requests": len(result["Requests"]), "Coverage": result["Coverage"]}))


if __name__ == "__main__":
    main()
