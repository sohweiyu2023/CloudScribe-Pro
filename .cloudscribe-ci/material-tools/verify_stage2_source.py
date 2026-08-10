#!/usr/bin/env python3
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

REQUIRED = (
    "src/CloudScribe.App/CloudScribeApplication.axaml",
    "src/CloudScribe.App/MainWindow.axaml",
    "src/CloudScribe.App/MainWindow.axaml.cs",
    "src/CloudScribe.App/MainWindow.VisualCapture.cs",
    "scripts/capture-stage2-windows.ps1",
    "scripts/verify-stage2.ps1",
    "scripts/verify-stage2.sh",
    "tools/verify_stage2_visual_evidence.py",
)


def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1


def require_text(text: str, marker: str, label: str) -> str | None:
    return None if marker in text else f"{label} missing required marker {marker!r}"


def main() -> int:
    root = Path.cwd().resolve()
    for relative in REQUIRED:
        if not (root / relative).is_file():
            return fail(f"Stage 2 source dependency missing: {relative}")

    capture = (root / "src/CloudScribe.App/MainWindow.VisualCapture.cs").read_text(encoding="utf-8-sig")
    main_window = (root / "src/CloudScribe.App/MainWindow.axaml.cs").read_text(encoding="utf-8-sig")
    validator = (root / "tools/verify_stage2_visual_evidence.py").read_text(encoding="utf-8-sig")
    app_theme = (root / "src/CloudScribe.App/CloudScribeApplication.axaml").read_text(encoding="utf-8-sig")
    architecture = (root / "tests/CloudScribe.Architecture.Tests/AdaptiveShellTests.cs").read_text(encoding="utf-8-sig")

    contracts = (
        (capture, "ISolidColorBrush", "visual capture solid-brush audit"),
        (capture, "captureCase.FocusEditor || captureCase.FocusReading", "Focus Reading evidence metadata"),
        (capture, "FocusManager?.Focus(null!", "deterministic unfocused capture path"),
        (capture, "CaptureEditorVisualAudit", "editor contrast audit"),
        (main_window, "PostFocus(DocumentEditor);", "Focus Reading editor focus handoff"),
        (architecture, "FocusReadingPreservesKeyboardFocusAndUsesAdaptiveTitleBarGeometry", "Focus Reading architecture regression"),
        (architecture, "verify_stage2_evidence_inventory.py", "material evidence inventory contract"),
        (app_theme, "TextBox", "editor theme policy"),
    )
    for text, marker, label in contracts:
        problem = require_text(text, marker, label)
        if problem:
            return fail(problem)

    capture_cases = re.findall(r'new\("(?P<name>\d{2}-[^"]+)"', capture)
    validator_cases = re.findall(r'^\s*"(?P<name>\d{2}-[^"]+)"\s*:', validator, flags=re.MULTILINE)
    if len(capture_cases) != 17 or len(set(capture_cases)) != 17:
        return fail(f"visual capture matrix must contain exactly 17 unique cases; got {len(capture_cases)}")
    if len(validator_cases) != 17 or len(set(validator_cases)) != 17:
        return fail(f"visual validator must contain exactly 17 unique cases; got {len(validator_cases)}")
    if capture_cases != validator_cases:
        return fail("visual capture matrix and strict validator case order/identity diverge")
    if "06-full-focus-reading" not in capture_cases or "17-minimum-window-text-scale-200" not in capture_cases:
        return fail("required Focus Reading/minimum-window visual cases are missing")

    focus_line = re.search(r'^\s*"06-full-focus-reading"\s*:\s*\((.*)\),\s*$', validator, flags=re.MULTILINE)
    if not focus_line or "True, False, True, False, 1.0" not in focus_line.group(1):
        return fail("strict validator does not require EditorFocused=True for Focus Reading case 06")

    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"SESSION_STATE.json invalid: {exc}")
    if state.get("current_stage") != 2 or state.get("stage2_source_implemented") is not True:
        return fail("SESSION_STATE.json does not identify implemented Stage 2 source")
    if str(state.get("repository_version", "")).split("-", 1)[0] != "0.3.48":
        return fail(f"Stage 2 source version is not 0.3.48: {state.get('repository_version')!r}")

    print("PASS: Stage 2 adaptive shell, 17-case visual matrix, editor solid-brush audit and Focus Reading focus contract verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
