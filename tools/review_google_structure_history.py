"""Offline, content-free review of retained structured Google transport events.

No provider access, profile mutation, automatic tuning, or causal/optimal-length claim.
Reads bounded JSONL; ignores free-form Message/exception fields entirely.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import json
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


def review(directory: Path, max_files=256, max_bytes=33_554_432):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("A physical existing log directory is required")
    all_files = sorted(directory.glob("*.jsonl"), key=lambda p: p.stat().st_mtime_ns, reverse=True)
    requests, identities = {}, {}
    read_bytes = bad_lines = skipped_files = incomplete_events = 0
    stamps = []
    scanned = 0
    for path in all_files[:max_files]:
        size = path.stat().st_size
        if path.is_symlink() or size > 4_194_304 or read_bytes + size > max_bytes:
            skipped_files += 1
            continue
        data = path.read_bytes()
        if len(data) != size:
            raise ValueError("Log changed during bounded snapshot; retry after a safe checkpoint")
        read_bytes += len(data)
        scanned += 1
        for line in reversed(data.splitlines()):
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
                    stamps.append(stamp)
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
            "Coverage": {"ScannedFiles": scanned, "OmittedFiles": max(0, len(all_files) - max_files) + skipped_files,
                         "ReadBytes": read_bytes, "MalformedLines": bad_lines, "IncompleteEvents": incomplete_events,
                         "ObservedFirstUnixMilliseconds": min(stamps, default=None),
                         "ObservedLastUnixMilliseconds": max(stamps, default=None),
                         "RequestsWithoutCompleteStructure": sum(not x.get("StructureComplete") for x in rows),
                         "RequestsWithoutTimeline": sum("FirstObservedUnixMilliseconds" not in x for x in rows),
                         "RequestsWithoutBuildIdentity": sum("ModuleId" not in x for x in rows),
                         "SourceRetention": "Content-free trend history has no automatic age/count rotation; routine logs may rotate. This bounded scan can omit sources (see coverage). Separate protected response text is not read."},
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
