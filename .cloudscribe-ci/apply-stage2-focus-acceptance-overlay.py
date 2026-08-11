from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import pathlib
import re
import subprocess
import sys
import tempfile

EXPECTED_PATCH_SHA256 = "1b7322cc773969e81f6519cbdc901ffd7daa9455928e68d336ceaef50035e4ac"
EXPECTED_FILES = {
    "src/CloudScribe.App/MainWindow.axaml": "146e7395924c757721de6e7e89d0a6f833192c861fd794ed2e64909ec0a9c65d",
    "src/CloudScribe.App/MainWindow.VisualCapture.cs": "d140d1836ee9070149afeafd6dd95c1e1f36deb2d3ab60033741f4b13c0d9a28",
    "tools/verify_stage2_visual_evidence.py": "756c8d387e4af76ceef10c7413d3356ac8018d04248b45777c7c887947b6629a",
    "tools/verify_stage2_source.py": "42bd813fa5e5d697fceec555e08b276cfc7da07df4c1f4b63d1eddb762d3fd57",
    "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs": "2badcd169ecaed5444f7b12fcd20ff304f6d93081d90f1a9426ef9edd9abd2fc",
    "tools/run_python_regression_shards.py": "03d277226cdf42a49070ffd5104231c798c5cdaa0be223063f0cdea33fd2deda",
    "SESSION_STATE.json": "7a4db331b97fde556892e01b15b44f251b38fb2130bdc1144c213c0db117855a",
}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run_git_apply(source_root: pathlib.Path, patch_path: pathlib.Path, *, check: bool) -> None:
    command = ["git", "apply"]
    if check:
        command.append("--check")
    command.extend(["--whitespace=nowarn", str(patch_path)])
    result = subprocess.run(command, cwd=source_root, check=False)
    if result.returncode != 0:
        phase = "preflight" if check else "apply"
        raise RuntimeError(f"Stage 2 focus repair patch {phase} failed: {result.returncode}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    parser.add_argument("--carrier", required=True)
    args = parser.parse_args()

    source_root = pathlib.Path(args.source_root).resolve()
    carrier = pathlib.Path(args.carrier).resolve()

    if not (source_root / "CloudScribe.sln").is_file():
        raise RuntimeError(f"CloudScribe source root is invalid: {source_root}")
    if not carrier.is_file():
        raise RuntimeError(f"Stage 2 focus repair carrier is missing: {carrier}")

    carrier_text = carrier.read_text(encoding="utf-8-sig")
    match = re.search(
        r"\$payloadBase64\s*=\s*@'\s*(.*?)\s*'@",
        carrier_text,
        flags=re.DOTALL,
    )
    if match is None:
        raise RuntimeError("Stage 2 focus repair payload was not found in the carrier.")

    encoded = re.sub(r"\s+", "", match.group(1))
    compressed = base64.b64decode(encoded, validate=True)
    patch_bytes = gzip.decompress(compressed)
    patch_sha = sha256_bytes(patch_bytes)
    if patch_sha != EXPECTED_PATCH_SHA256:
        raise RuntimeError(
            "Stage 2 focus repair patch hash mismatch: "
            f"expected={EXPECTED_PATCH_SHA256} actual={patch_sha}"
        )

    with tempfile.NamedTemporaryFile(prefix="cloudscribe-stage2-focus-", suffix=".patch", delete=False) as temp:
        temp.write(patch_bytes)
        patch_path = pathlib.Path(temp.name)

    try:
        run_git_apply(source_root, patch_path, check=True)
        run_git_apply(source_root, patch_path, check=False)
    finally:
        patch_path.unlink(missing_ok=True)

    for relative, expected in EXPECTED_FILES.items():
        target = source_root / relative
        if not target.is_file():
            raise RuntimeError(f"Stage 2 focus repair output is missing: {relative}")
        actual = sha256_file(target)
        if actual != expected:
            raise RuntimeError(
                f"Stage 2 focus repair postimage mismatch for {relative}: "
                f"expected={expected} actual={actual}"
            )

    print(
        "CLOUDSCRIBE_STAGE2_FOCUS_ACCEPTANCE_REPAIR=PASS "
        f"patch_sha256={patch_sha} transport=python-gzip"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise
