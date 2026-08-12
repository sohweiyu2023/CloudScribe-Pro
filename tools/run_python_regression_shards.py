#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

SHARD_COUNT = 15
EXPECTED_CHECK_COUNT = 151
PROJECTS = (
    "src/CloudScribe.App/CloudScribe.App.csproj",
    "src/CloudScribe.Application/CloudScribe.Application.csproj",
    "src/CloudScribe.Domain/CloudScribe.Domain.csproj",
    "src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj",
    "src/CloudScribe.Providers.Abstractions/CloudScribe.Providers.Abstractions.csproj",
    "tests/CloudScribe.Domain.Tests/CloudScribe.Domain.Tests.csproj",
    "tests/CloudScribe.Application.Tests/CloudScribe.Application.Tests.csproj",
    "tests/CloudScribe.Infrastructure.Tests/CloudScribe.Infrastructure.Tests.csproj",
    "tests/CloudScribe.Architecture.Tests/CloudScribe.Architecture.Tests.csproj",
)
TOOLS = (
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
)
SCRIPTS = (
    "scripts/capture-stage2-linux.sh",
    "scripts/capture-stage2-windows.ps1",
    "scripts/invoke-nuget-audit-scan.ps1",
    "scripts/invoke-nuget-audit-scan.sh",
    "scripts/publish-stage2-windows.ps1",
    "scripts/smoke-stage1-windows.ps1",
    "scripts/verify-stage2.ps1",
    "scripts/verify-stage2.sh",
)
ROOT_FILES = (
    "CloudScribe.sln",
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json",
    "NuGet.config",
    "SESSION_STATE.json",
    "SHA256SUMS.txt",
    "BUILD-CLOUDSCRIBE-WINDOWS.cmd",
    "BUILDING-WINDOWS.txt",
)


@dataclass(frozen=True)
class Check:
    name: str
    run: Callable[[], None]


def assert_true(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def subprocess_check(root: Path, command: list[str], timeout: int = 180) -> None:
    result = subprocess.run(
        command,
        cwd=root,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=timeout,
        check=False,
    )
    if result.returncode != 0:
        raise AssertionError(f"command failed ({result.returncode}): {' '.join(command)}\n{result.stdout[-4000:]}")


def build_checks(root: Path) -> list[Check]:
    checks: list[Check] = []

    def add(name: str, fn: Callable[[], None]) -> None:
        checks.append(Check(name, fn))

    for rel in PROJECTS:
        add(f"project exists: {rel}", lambda rel=rel: assert_true((root / rel).is_file(), f"missing {rel}"))
    for rel in PROJECTS:
        lock = str(Path(rel).parent / "packages.lock.json")
        add(f"lock exists: {lock}", lambda lock=lock: assert_true((root / lock).is_file(), f"missing {lock}"))
    for rel in PROJECTS:
        def parse_project(rel: str = rel) -> None:
            ET.parse(root / rel)
        add(f"project XML parses: {rel}", parse_project)
    for rel in PROJECTS:
        lock = str(Path(rel).parent / "packages.lock.json")
        def parse_lock(lock: str = lock) -> None:
            payload = json.loads((root / lock).read_text(encoding="utf-8-sig"))
            assert_true(isinstance(payload, dict) and isinstance(payload.get("dependencies"), dict), f"invalid lock shape: {lock}")
        add(f"lock JSON parses: {lock}", parse_lock)

    for rel in TOOLS:
        add(f"tool exists: {rel}", lambda rel=rel: assert_true((root / rel).is_file(), f"missing {rel}"))
    for rel in SCRIPTS:
        add(f"script exists: {rel}", lambda rel=rel: assert_true((root / rel).is_file(), f"missing {rel}"))
    for rel in ROOT_FILES:
        add(f"root file exists: {rel}", lambda rel=rel: assert_true((root / rel).is_file(), f"missing {rel}"))

    capture_path = root / "src/CloudScribe.App/MainWindow.VisualCapture.cs"
    validator_path = root / "tools/verify_stage2_visual_evidence.py"
    capture = capture_path.read_text(encoding="utf-8-sig")
    validator = validator_path.read_text(encoding="utf-8-sig")
    capture_names = [line.split('new("', 1)[1].split('"', 1)[0] for line in capture.splitlines() if 'new("' in line and line.split('new("', 1)[1][:2].isdigit()]
    validator_names = [line.strip().split('"', 2)[1] for line in validator.splitlines() if line.strip().startswith('"') and len(line.strip()) > 3 and line.strip()[1:3].isdigit() and '":' in line]
    assert_true(len(capture_names) == 17, f"runner bootstrap expected 17 capture cases, found {len(capture_names)}")
    assert_true(len(validator_names) == 17, f"runner bootstrap expected 17 validator cases, found {len(validator_names)}")

    for name in capture_names:
        add(f"capture case present once: {name}", lambda name=name: assert_true(capture.count(f'new("{name}"') == 1, f"capture case count mismatch: {name}"))
    for name in validator_names:
        add(f"validator case present once: {name}", lambda name=name: assert_true(validator.count(f'"{name}"') >= 1, f"validator case missing: {name}"))

    main_window = (root / "src/CloudScribe.App/MainWindow.axaml.cs").read_text(encoding="utf-8-sig")
    main_window_xaml = (root / "src/CloudScribe.App/MainWindow.axaml").read_text(encoding="utf-8-sig")
    app_theme = (root / "src/CloudScribe.App/CloudScribeApplication.axaml").read_text(encoding="utf-8-sig")
    architecture = (root / "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs").read_text(encoding="utf-8-sig")
    stage2_ps = (root / "scripts/verify-stage2.ps1").read_text(encoding="utf-8-sig")
    marker_specs = (
        (capture, "ISolidColorBrush", "interface solid-brush audit"),
        (capture, "captureCase.FocusEditor || captureCase.FocusReading", "Focus Reading metadata"),
        (capture, "CaptureEditorVisualAudit", "editor visual audit"),
        (capture, "FocusManager?.Focus(null!", "deterministic focus clear"),
        (capture, "SetVisualCapturePointerOver", "bounded pointer-over pseudoclass seam"),
        (validator, '"06-full-focus-reading"', "Focus Reading validator case"),
        (validator, "EditorFocused", "editor focus validator field"),
        (main_window, "PostFocus(DocumentEditor);", "product editor focus handoff"),
        (architecture, "FocusReadingPreservesKeyboardFocusAndUsesAdaptiveTitleBarGeometry", "focus architecture regression"),
        (architecture, "verify_stage2_evidence_inventory.py", "evidence inventory architecture contract"),
        (main_window_xaml, "PART_PaperSurface", "paper editor dedicated template-surface policy"),
        (stage2_ps, "STEP {1:00}", "bounded command ledger numbering"),
    )
    for text, marker, label in marker_specs:
        add(f"source marker: {label}", lambda text=text, marker=marker: assert_true(marker in text, f"missing marker {marker!r}"))

    state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    global_json = json.loads((root / "global.json").read_text(encoding="utf-8-sig"))
    session_specs = (
        (state.get("project") == "CloudScribe Pro", "project identity"),
        (state.get("current_stage") == 2, "current stage"),
        (str(state.get("repository_version", "")).startswith("0.3.48-"), "repository version"),
        (state.get("required_dotnet_sdk") == "10.0.400", "required SDK"),
        (global_json.get("sdk", {}).get("version") == state.get("required_dotnet_sdk"), "global/session SDK consistency"),
        (state.get("stage1_checkpoint_promoted") is True, "Stage 1 checkpoint promoted"),
        (state.get("stage2_source_implemented") is True and state.get("stage2_runtime_tested") is True and state.get("stage2_windows_ui_tested") is True, "Stage 2 source and automated Windows engineering verification recorded"),
        (state.get("stage2_promotion_blocked") is True and state.get("stage2_manual_visual_acceptance") is False and state.get("stage2_user_clicked_editor_retest") is False, "manual promotion remains blocked pending real-user editor acceptance"),
        (state.get("stage_gate_passed") is False, "stage gate not falsely claimed"),
        (state.get("whole_application_final_claimed") is False, "whole application not falsely final"),
    )
    for condition, label in session_specs:
        add(f"session state: {label}", lambda condition=condition, label=label: assert_true(condition, f"session state failed: {label}"))

    for rel in PROJECTS:
        lock_path = root / Path(rel).parent / "packages.lock.json"
        def lock_net10(lock_path: Path = lock_path) -> None:
            payload = json.loads(lock_path.read_text(encoding="utf-8-sig"))
            keys = set(payload.get("dependencies", {}))
            assert_true(any(key.startswith("net10.0") for key in keys), f"net10.0 graph missing: {lock_path}")
        add(f"lock targets net10.0: {rel}", lock_net10)

    project_set = {rel for rel in PROJECTS}
    for rel in PROJECTS:
        def refs_valid(rel: str = rel) -> None:
            project = root / rel
            tree = ET.parse(project)
            for node in tree.getroot().iter():
                if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
                    continue
                include = node.attrib.get("Include", "")
                target = (project.parent / include).resolve()
                try:
                    target_rel = target.relative_to(root).as_posix()
                except ValueError as exc:
                    raise AssertionError(f"reference escapes repository: {rel}: {include}") from exc
                assert_true(target_rel in project_set and target.is_file(), f"reference target missing: {rel}: {include}")
                assert_true(not (rel.startswith("src/") and target_rel.startswith("tests/")), f"production->test reference: {rel}->{target_rel}")
        add(f"project references valid: {rel}", refs_valid)

    manifest_path = root / "SHA256SUMS.txt"
    manifest_lines = [line.strip() for line in manifest_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    manifest_paths = [line.split("  ", 1)[1] if "  " in line else line.split(" *", 1)[-1] for line in manifest_lines]
    add("manifest has substantial inventory", lambda: assert_true(len(manifest_lines) >= 140, f"manifest too small: {len(manifest_lines)}"))
    add("manifest has unique paths", lambda: assert_true(len(manifest_paths) == len(set(manifest_paths)), "duplicate manifest paths"))
    add("manifest paths are safe relative paths", lambda: assert_true(all(p and not p.startswith(("/", "\\")) and ".." not in Path(p).parts for p in manifest_paths), "unsafe manifest path"))
    add("manifest includes restored material runner", lambda: assert_true(any(p.endswith("tools/run_python_regression_shards.py") for p in manifest_paths), "material runner absent from manifest"))
    add("manifest updater check passes", lambda: subprocess_check(root, [sys.executable, "tools/update_sha256_manifest.py", "--check"], 120))

    add("project dependency verifier passes", lambda: subprocess_check(root, [sys.executable, "tools/verify_project_dependencies.py"], 120))
    add("Stage 1 source verifier passes", lambda: subprocess_check(root, [sys.executable, "tools/verify_stage1_source.py"], 120))
    add("Stage 2 source verifier passes", lambda: subprocess_check(root, [sys.executable, "tools/verify_stage2_source.py"], 120))
    add("repository governance verifier passes", lambda: subprocess_check(root, [sys.executable, "tools/verify_repository.py"], 180))
    add("capture and validator case lists are identical", lambda: assert_true(capture_names == validator_names, "capture/validator case identity or order mismatch"))

    if len(checks) != EXPECTED_CHECK_COUNT:
        raise RuntimeError(f"internal regression inventory error: expected {EXPECTED_CHECK_COUNT} checks, built {len(checks)}")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser(description="Run CloudScribe's deterministic 151-check Stage 2 Python/material regression inventory in 15 stable shards.")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true", help="run all 15 shards")
    group.add_argument("--shard", type=int, choices=range(1, SHARD_COUNT + 1), metavar="1..15")
    args = parser.parse_args()

    root = Path.cwd().resolve()
    try:
        checks = build_checks(root)
    except Exception as exc:
        print(f"FAIL: regression inventory could not be constructed: {exc}", file=sys.stderr)
        return 2

    selected = checks if args.all else [check for index, check in enumerate(checks) if index % SHARD_COUNT == args.shard - 1]
    failures: list[str] = []
    for index, check in enumerate(selected, 1):
        try:
            check.run()
            print(f"PASS {index:03}/{len(selected):03} {check.name}")
        except Exception as exc:
            failures.append(f"{check.name}: {exc}")
            print(f"FAIL {index:03}/{len(selected):03} {check.name}: {exc}", file=sys.stderr)

    if failures:
        print(f"FAIL: {len(failures)} of {len(selected)} selected regression checks failed.", file=sys.stderr)
        return 1
    if args.all:
        print(f"PASS: {EXPECTED_CHECK_COUNT}/{EXPECTED_CHECK_COUNT} material regression checks across {SHARD_COUNT} deterministic shards.")
    else:
        print(f"PASS: shard {args.shard}/{SHARD_COUNT}: {len(selected)}/{len(selected)} checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
