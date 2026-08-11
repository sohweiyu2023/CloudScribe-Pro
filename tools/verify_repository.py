#!/usr/bin/env python3
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

REQUIRED_ROOT = (
    "CloudScribe.sln",
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json",
    "NuGet.config",
    "SESSION_STATE.json",
    "SHA256SUMS.txt",
)
REQUIRED_TOOLS = (
    "tools/prepare_physical_directory.py",
    "tools/run_bounded_process.py",
    "tools/update_sha256_manifest.py",
    "tools/verify_dotnet_package_scan.py",
    "tools/verify_stage2_visual_evidence.py",
    "tools/verify_dotnet_sdk_version.py",
    "tools/verify_project_dependencies.py",
    "tools/verify_repository.py",
    "tools/verify_stage1_source.py",
    "tools/verify_stage2_source.py",
    "tools/verify_stage2_evidence_inventory.py",
    "tools/run_python_regression_shards.py",
    "tools/create_source_archive.py",
    "tools/verify_source_release.py",
    "tools/run_verifier_self_tests.py",
)
REQUIRED_TESTS = ("tests/test_verification_tools.py",)


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def main() -> int:
    root = Path.cwd().resolve()
    for relative in (*REQUIRED_ROOT, *REQUIRED_TOOLS, *REQUIRED_TESTS):
        if not (root / relative).is_file():
            return fail(f"required release-source file missing: {relative}")

    try:
        session = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
        global_json = json.loads((root / "global.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"invalid repository JSON contract: {exc}")

    if session.get("project") != "CloudScribe Pro" or session.get("current_stage") != 2:
        return fail("SESSION_STATE.json does not identify CloudScribe Pro Stage 2")
    version = str(session.get("repository_version", ""))
    if not version.startswith("0.3.48-"):
        return fail(f"unexpected repository version: {version!r}")

    sdk_contract = global_json.get("sdk")
    if not isinstance(sdk_contract, dict):
        return fail("global.json is missing its sdk object")
    required_sdk = str(session.get("required_dotnet_sdk", ""))
    configured_sdk = str(sdk_contract.get("version", ""))
    if not required_sdk or required_sdk != configured_sdk:
        return fail(f"SDK contract mismatch: session={required_sdk!r} global.json={configured_sdk!r}")
    if sdk_contract.get("rollForward") != "disable":
        return fail("global.json sdk.rollForward must be 'disable' for exact SDK + package-lock reproducibility")
    if sdk_contract.get("allowPrerelease") is not False:
        return fail("global.json sdk.allowPrerelease must be false for the stable release toolchain")

    if session.get("stage1_checkpoint_promoted") is not True or session.get("stage2_source_implemented") is not True:
        return fail("stage checkpoint/source-state flags are inconsistent")
    if session.get("whole_application_final_claimed") is not False:
        return fail("source incorrectly claims the whole application is final")

    self_contained = session.get("controlling_context_self_contained")
    context_manifest = session.get("controlling_context_manifest")
    immutable_package = session.get("immutable_master_package_present")
    if self_contained is True:
        if not isinstance(context_manifest, str) or not context_manifest.strip() or not (root / context_manifest).is_file():
            return fail("SESSION_STATE.json claims self-contained controlling context but its manifest is missing")
        if immutable_package is not True:
            return fail("self-contained controlling context requires immutable_master_package_present=true")
    elif self_contained is False:
        if context_manifest not in (None, ""):
            return fail("external controlling context must not advertise a dangling in-source context manifest")
        if immutable_package is not False:
            return fail("external controlling context must set immutable_master_package_present=false")
    else:
        return fail("controlling_context_self_contained must be an explicit boolean")

    for path in root.rglob("*"):
        try:
            if path.is_symlink():
                return fail(f"symbolic link present in release source: {path.relative_to(root)}")
            if hasattr(path, "is_junction") and path.is_junction():
                return fail(f"junction present in release source: {path.relative_to(root)}")
        except OSError as exc:
            return fail(f"filesystem metadata could not be inspected for {path}: {exc}")

    result = subprocess.run(
        [sys.executable, "tools/update_sha256_manifest.py", "--check"],
        cwd=root,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=120,
        check=False,
        env={**__import__("os").environ, "PYTHONDONTWRITEBYTECODE": "1"},
    )
    if result.returncode != 0:
        return fail("repository SHA-256 manifest verification failed:\n" + result.stdout[-4000:])

    print(
        f"PASS: repository governance, exact SDK state, external-context truthfulness, "
        f"physical-source policy and SHA-256 manifest verified for {version}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
