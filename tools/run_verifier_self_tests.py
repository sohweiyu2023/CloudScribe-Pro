#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE_TEST_CLASSES = (
    ("DotnetSdkVersionPolicyTests", 8),
    ("PackageScanValidatorTests", 6),
    ("PhysicalDirectoryToolTests", 4),
    ("RepositoryVerifierTests", 5),
    ("SourceArchiveCliTests", 2),
    ("SourceManifestToolTests", 3),
    ("Stage2EvidenceInventoryCliTests", 4),
    ("Stage2SourceContractTests", 4),
    ("Stage4SourceContractTests", 31),
    ("VisualEvidencePngParserTests", 9),
)
# Process-tree teardown semantics are host-specific. Run these as part of the
# certification suite on Windows, where CloudScribe ships and where taskkill /T
# gives a deterministic descendant cleanup primitive. Linux/macOS still execute
# the 45 portable verifier tests without leaving orphan/zombie children in CI.
TEST_CLASSES = BASE_TEST_CLASSES + (("ZBoundedProcessRunnerTests", 10),) if os.name == "nt" else BASE_TEST_CLASSES
RAN_RE = re.compile(r"Ran\s+(\d+)\s+tests?\s+in")


def _copy_tracked_source(destination: Path) -> None:
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"git ls-files failed: {result.stderr.decode(errors='replace')}")
    for raw_relative in result.stdout.split(b"\0"):
        if not raw_relative:
            continue
        relative = Path(os.fsdecode(raw_relative))
        source = ROOT / relative
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)


def _stage4_contract_root() -> tempfile.TemporaryDirectory[str]:
    temporary = tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-verifier-history-")
    root = Path(temporary.name)
    _copy_tracked_source(root)
    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    if state.get("project") != "CloudScribe Pro":
        temporary.cleanup()
        raise RuntimeError("Final source does not preserve CloudScribe Pro project identity")
    if state.get("current_stage") != 8 or state.get("repository_version") != "1.0.0":
        temporary.cleanup()
        raise RuntimeError("Historical Stage4 self-test projection is allowed only from exact Final Stage8 1.0.0 source")
    state["current_stage"] = 4
    state["repository_version"] = "0.5.0-stage4-preservation-self-test"
    state_path.write_text(json.dumps(state, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    return temporary


def main() -> int:
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    total = 0
    for class_name, expected in TEST_CLASSES:
        command = [
            sys.executable,
            "-B",
            "-m",
            "unittest",
            "-q",
            f"tests.test_verification_tools.{class_name}",
        ]
        temporary: tempfile.TemporaryDirectory[str] | None = None
        try:
            cwd = ROOT
            if class_name == "Stage4SourceContractTests":
                temporary = _stage4_contract_root()
                cwd = Path(temporary.name)
            result = subprocess.run(
                command,
                cwd=cwd,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=120,
                check=False,
                env=environment,
            )
        except (OSError, RuntimeError, subprocess.TimeoutExpired) as exc:
            print(f"FAIL: verifier self-test class could not run: {class_name}: {exc}", file=sys.stderr)
            return 1
        finally:
            if temporary is not None:
                temporary.cleanup()
        output = result.stdout + result.stderr
        if result.returncode != 0:
            print(f"FAIL: verifier self-test class failed: {class_name}\n{output[-8000:]}", file=sys.stderr)
            return 1
        match = RAN_RE.search(output)
        if not match:
            print(f"FAIL: verifier self-test result count missing for {class_name}\n{output[-4000:]}", file=sys.stderr)
            return 1
        actual = int(match.group(1))
        if actual != expected:
            print(
                f"FAIL: verifier self-test count drift for {class_name}: expected={expected} actual={actual}",
                file=sys.stderr,
            )
            return 1
        total += actual
        print(f"PASS: {class_name} {actual}/{expected}")

    expected_total = sum(expected for _, expected in TEST_CLASSES)
    if total != expected_total:
        print(f"FAIL: verifier self-test aggregate drift: expected={expected_total} actual={total}", file=sys.stderr)
        return 1
    scope = "Windows including process-tree defenses" if os.name == "nt" else "portable non-Windows verifier scope"
    print(f"PASS: {total}/{expected_total} isolated auxiliary Python verifier self-tests ({scope}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
