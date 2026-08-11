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
    required_cases = {
        "01-full-follow-system-dark-pointer-focus",
        "06-full-focus-reading",
        "07-compact-follow-system-light-pointer-focus",
        "17-minimum-window-text-scale-200",
    }
    if not required_cases.issubset(capture_cases):
        return fail("required Follow System pointer/focus, Focus Reading, or minimum-window visual cases are missing")
    if "SetVisualCapturePointerOver" not in capture or "SystemUsesDark" not in capture:
        return fail("visual capture does not exercise the bounded pointer-over pseudoclass seam plus deterministic Follow System state")
    window = (root / "src/CloudScribe.App/MainWindow.axaml").read_text(encoding="utf-8-sig")
    paper_theme = re.search(
        r'<ControlTheme\s+[^>]*x:Key="PaperTextBoxTheme"[^>]*>',
        window,
        flags=re.DOTALL,
    )
    if (
        not paper_theme
        or 'TargetType="controls:PaperTextBox"' not in paper_theme.group(0)
        or 'BasedOn="{StaticResource {x:Type TextBox}}"' not in paper_theme.group(0)
    ):
        return fail("paper editor does not own a derived PaperTextBox control theme")
    if 'ControlTemplate TargetType="controls:PaperTextBox"' not in window or 'Name="PART_PaperSurface"' not in window:
        return fail("paper editor must own a dedicated template surface rather than Fluent PART_BorderElement")
    if 'string.Equals(border.Name, "PART_PaperSurface", StringComparison.Ordinal)' not in capture:
        return fail("visual capture does not audit the dedicated paper template surface")
    paper_text_box = (root / "src/CloudScribe.App/Controls/PaperTextBox.cs").read_text(encoding="utf-8-sig")
    if 'PseudoClasses.Set(":pointerover", value)' not in paper_text_box:
        return fail("PaperTextBox capture seam does not toggle Avalonia's inherited :pointerover pseudoclass")
    paper_runtime_contracts = (
        ('OnApplyTemplate(TemplateAppliedEventArgs e)', "paper editor template-application hardening"),
        ('e.NameScope.Find<Border>("PART_PaperSurface")', "paper editor template-surface lookup"),
        ('TryFindResource("Brush.Paper"', "paper editor semantic paper resource pin"),
        ('_paperSurface.Background = paper;', "paper editor local template-surface background pin"),
        ('Foreground = ink;', "paper editor local ink pin"),
        ('CaretBrush = ink;', "paper editor local caret pin"),
        ('SelectionForegroundBrush = ink;', "paper editor local selection-foreground pin"),
        ('SelectionBrush = selection;', "paper editor local selection-background pin"),
        ('PlaceholderForeground = muted;', "paper editor local placeholder pin"),
        ('ResourcesChanged +=', "paper editor live resource refresh"),
        ('ActualThemeVariantChanged +=', "paper editor theme-variant refresh"),
    )
    for marker, label in paper_runtime_contracts:
        if marker not in paper_text_box:
            return fail(f"{label} missing required marker {marker!r}")

    focus_line = re.search(r'^\s*"06-full-focus-reading"\s*:\s*\((.*)\),\s*$', validator, flags=re.MULTILINE)
    if not focus_line or "True, False, True, False, None, False, 1.0" not in focus_line.group(1):
        return fail("strict validator does not require EditorFocused=True for Focus Reading case 06")

    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"SESSION_STATE.json invalid: {exc}")
    if state.get("current_stage") != 2 or state.get("stage2_source_implemented") is not True:
        return fail("SESSION_STATE.json does not identify implemented Stage 2 source")
    if str(state.get("repository_version", "")).split("-", 1)[0] != "0.3.48":
        return fail(f"Stage 2 source version is not 0.3.48: {state.get('repository_version')!r}")

    if state.get("stage2_runtime_tested") is not True or state.get("stage2_windows_ui_tested") is not True:
        return fail("SESSION_STATE.json does not record completed automated/native Windows Stage 2 engineering verification")
    if state.get("stage2_manual_visual_acceptance") is not False or state.get("stage2_user_clicked_editor_retest") is not False:
        return fail("SESSION_STATE.json must keep real-user Stage 2 acceptance pending until the user verifies the repaired editor")
    state_text = json.dumps(state, ensure_ascii=False)
    for stale in ("Exact 0.3.47", "Exact 0.3.48 bytes have not executed", "Exact 0.3.48 runtime remains pending", "Apply the Stage 2 user-acceptance focus repair"):
        if stale in state_text:
            return fail(f"SESSION_STATE.json retains stale pre-focus-repair state: {stale}")

    print("PASS: Stage 2 adaptive shell, 17-case visual matrix, Follow System pointer/focus paper-theme regression, editor rendered/brush contrast and Focus Reading contracts verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
