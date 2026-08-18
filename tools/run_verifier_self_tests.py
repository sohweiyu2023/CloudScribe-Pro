#!/usr/bin/env python3
from __future__ import annotations

import os
import re
import subprocess
import sys
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
    ("Stage4SourceContractTests", 25),
    ("VisualEvidencePngParserTests", 9),
)
# Process-tree teardown semantics are host-specific. Run these as part of the
# certification suite on Windows, where CloudScribe ships and where taskkill /T
# gives a deterministic descendant cleanup primitive. Linux/macOS still execute
# the 45 portable verifier tests without leaving orphan/zombie children in CI.
TEST_CLASSES = BASE_TEST_CLASSES + (("ZBoundedProcessRunnerTests", 10),) if os.name == "nt" else BASE_TEST_CLASSES
RAN_RE = re.compile(r"Ran\s+(\d+)\s+tests?\s+in")


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
        try:
            result = subprocess.run(
                command,
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=120,
                check=False,
                env=environment,
            )
        except subprocess.TimeoutExpired as exc:
            print(f"FAIL: verifier self-test class timed out: {class_name}: {exc}", file=sys.stderr)
            return 1
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
