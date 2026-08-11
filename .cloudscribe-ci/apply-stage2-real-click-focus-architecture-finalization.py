from __future__ import annotations

import argparse
import hashlib
from pathlib import Path

RELATIVE = "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs"
PRE_SHA256 = "f9e63983cdab8da87f7b8c361bf6a40bd884f26772bff4e082eda87813e962b7"
POST_SHA256 = "e3e7fd7ce5fd369401005ab36d48ec41ce2a2ff089b4494f2bd548228ce8dff5"
OLD = '        Assert.Contains("PART_BorderElement", capture, StringComparison.Ordinal);'
NEW = '        Assert.Contains("PART_PaperSurface", capture, StringComparison.Ordinal);'


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    root = Path(parser.parse_args().source_root).resolve()
    path = root / RELATIVE
    actual = sha256(path)
    if actual == POST_SHA256:
        print("CLOUDSCRIBE_STAGE2_REAL_CLICK_FOCUS_ARCHITECTURE_FINALIZATION=PASS already_applied=true")
        return 0
    if actual != PRE_SHA256:
        raise RuntimeError(f"unexpected architecture preimage: expected={PRE_SHA256} actual={actual}")
    text = path.read_text(encoding="utf-8")
    if text.count(OLD) != 1:
        raise RuntimeError("expected exactly one stale PART_BorderElement architecture assertion")
    path.write_text(text.replace(OLD, NEW, 1), encoding="utf-8", newline="")
    actual = sha256(path)
    if actual != POST_SHA256:
        raise RuntimeError(f"architecture postimage mismatch: expected={POST_SHA256} actual={actual}")
    print("CLOUDSCRIBE_STAGE2_REAL_CLICK_FOCUS_ARCHITECTURE_FINALIZATION=PASS already_applied=false")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
