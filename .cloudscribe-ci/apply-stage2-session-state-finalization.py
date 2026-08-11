from __future__ import annotations

import argparse
import hashlib
import json
import pathlib

PREIMAGE_SHA256 = {
    "SESSION_STATE.json": "7a4db331b97fde556892e01b15b44f251b38fb2130bdc1144c213c0db117855a",
    "tools/verify_stage2_source.py": "d8c69570db150022c221f9e9dcd8efaa847019bb936a46c05974835ffc70e635",
    "tools/run_python_regression_shards.py": "62c10f15a1f8e2c39796d3863e9b3f7e462f37da30999a5b2577b5703cf67ca5",
}

POSTIMAGE_SHA256 = {
    "SESSION_STATE.json": "c7b6194505ab229f47bc1577e233c5fbee03ecc8fe03b5466417a14b19819496",
    "tools/verify_stage2_source.py": "862586d5730d5f0ee4a1957924e261e2a0f95598d1a697c2797a28fc804c1d45",
    "tools/run_python_regression_shards.py": "c3e2e45c140c96ec0ce9634b050c6ac91f147c09dd37febb6b4e7a7662996e00",
}

STATE_UPDATES = {
    "generated_at_utc": "2026-08-11T02:40:24Z",
    "status": "Stage 2 focus/readability repair implemented and automated/native Windows engineering verification completed for the repaired product state. Promotion remains blocked only on real-user/manual visual acceptance and OS-level accessibility acceptance. Exact certification run IDs and archive checksums are external handoff evidence rather than self-referential source state.",
    "stage2_static_source_gate": "passed on the final focus-repair source: PaperTextBox control-theme ownership, Follow System dark/light pointer+focus cases, rendered-center/brush contrast, build-launcher and material contracts are source-enforced",
    "stage2_locked_restore": "0.3.48 focus-repair candidate passed all nine locked restores in native Windows certification; every handoff must still carry exact frozen-archive evidence.",
    "stage2_debug_build": "0.3.48 focus-repair candidate built all nine projects in Debug with zero warnings/errors in native Windows certification.",
    "stage2_release_build": "0.3.48 focus-repair candidate built all nine projects in Release with zero warnings/errors in native Windows certification.",
    "stage2_dotnet_tests": "0.3.48 focus-repair candidate passed 147/147 .NET tests: Domain 6, Application 8, Infrastructure 64 and Architecture 69.",
    "stage2_runtime_tested": True,
    "stage2_runtime_screenshots": True,
    "stage2_windows_ui_tested": True,
    "stage2_automated_windows_certification": True,
    "stage2_manual_visual_acceptance": False,
    "stage2_manual_accessibility_acceptance": False,
    "stage2_user_clicked_editor_retest": False,
    "stage2_promotion_blocked": True,
    "stage_gate_passed": False,
    "application_runtime_tested": True,
    "application_runtime_evidence_scope": "current Stage 2 focus-repair candidate plus the immutable promoted Stage 1 checkpoint",
    "application_runtime_platform": "Windows Server 2025 automated/native certification; real-user Windows visual/accessibility acceptance remains separate",
    "windows_ui_tested": True,
    "open_findings": 0,
    "open_P0": 0,
    "open_P1": 0,
    "open_P2": 0,
    "next_stage": 2,
    "next_exact_action": "Build/run the exact certified focus-repair ZIP on the user Windows machine, select Follow system, click/hover the document editor and verify readable paper/ink/caret/selection; complete the remaining manual High Contrast/Narrator/text-scale/mixed-DPI acceptance as applicable. If accepted, promote the exact Stage 2 bytes without product changes and create the Stage 3 checkpoint. Do not start Stage 3 before that acceptance.",
    "bytes_changed_after_latest_reaudit": False,
    "latest_reaudit_completed_at_utc": "2026-08-11T02:40:24Z",
    "artifact_sha256": "external exact-archive checksum required beside every handoff ZIP; intentionally not self-referential inside source",
    "stage2_python_regressions": "canonical 151-check/15-shard deterministic material inventory passes on the final focus-repair source and is rerun from every fresh-extracted handoff ZIP",
    "stage2_visual_matrix": "17 real-runtime Windows cases across required widths/lifecycle/theme/scaling states; cases 01 and 07 explicitly exercise deterministic Follow System dark/light with simultaneous :pointerover + focus, and the validator checks editor brushes plus rendered center brightness. OS text-scale/mixed-DPI/manual-accessibility remain explicit non-claims.",
    "source_csharp_sanity": "passed across 94 UTF-8 C# source files using lexical/delimiter, invalid-escape, import-order, shallow-indentation and top-level type/file-name checks; bounded analyzer parity remains explicitly non-Roslyn",
    "findings_closed": 184,
    "source_archive_delivery_policy": "tools/create_source_archive.py binds the exact archive filename to internal version markers before deterministic no-overwrite publication; tools/verify_source_release.py validates checksum, safe clean extraction, exact source/archive bytes, the complete 151-check suite and post-test manifest immutability",
    "stage2_shell_behavior_contract": "truthful lifecycle surfaces, hidden future controls, normalized shell state, idempotent platform subscriptions, complete Focus Reading isolation and finite nonnegative adaptive viewport geometry are preserved; systemic gradients, layered surfaces, bespoke button/checkbox themes, semantic scrims, reduced-motion/high-contrast classes and the paper-editor pointer/focus contract are source/runtime verified. Manual user acceptance remains the promotion boundary.",
    "stage2_visual_redesign": "0.3.48 now includes the real-user focus/readability repair: document title/body use a derived PaperTextBox control theme that owns paper background/ink/caret/selection/placeholder states, and Follow System dark/light pointer+focus captures are part of the 17-case Windows evidence matrix.",
    "windows_stage2_execution_evidence": "Exact-byte native Windows certification is mandatory for every handoff and is retained outside the source snapshot with the ZIP checksum/evidence bundle. The current source truthfully records engineering verification while keeping user/manual acceptance separate.",
    "avalonia_patch": "Avalonia.Desktop, Avalonia.Themes.Fluent and their exact transitive family remain locked to 12.1.1 with NuGet canonical signed-package content hashes; the current Stage 2 focus-repair candidate compiles and runs under native Windows automation, while real-user manual acceptance remains separate.",
}


def sha256_file(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_lf(path: pathlib.Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")


def write_lf(path: pathlib.Path, text: str) -> None:
    path.write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label} replacement expected exactly once; found {count}")
    return text.replace(old, new, 1)


def verify_hashes(root: pathlib.Path, expected: dict[str, str], phase: str) -> None:
    for relative, expected_hash in expected.items():
        path = root / relative
        if not path.is_file():
            raise RuntimeError(f"{phase} file missing: {relative}")
        actual = sha256_file(path)
        if actual != expected_hash:
            raise RuntimeError(
                f"{phase} hash mismatch for {relative}: expected={expected_hash} actual={actual}"
            )


def finalize_state(root: pathlib.Path) -> None:
    path = root / "SESSION_STATE.json"
    state = json.loads(path.read_text(encoding="utf-8-sig"))
    state.update(STATE_UPDATES)
    write_lf(path, json.dumps(state, ensure_ascii=False, indent=2) + "\n")


def strengthen_source_verifier(root: pathlib.Path) -> None:
    path = root / "tools/verify_stage2_source.py"
    text = read_lf(path)
    anchor = '''    if str(state.get("repository_version", "")).split("-", 1)[0] != "0.3.48":\n        return fail(f"Stage 2 source version is not 0.3.48: {state.get('repository_version')!r}")\n\n'''
    addition = anchor + '''    if state.get("stage2_runtime_tested") is not True or state.get("stage2_windows_ui_tested") is not True:\n        return fail("SESSION_STATE.json does not record completed automated/native Windows Stage 2 engineering verification")\n    if state.get("stage2_manual_visual_acceptance") is not False or state.get("stage2_user_clicked_editor_retest") is not False:\n        return fail("SESSION_STATE.json must keep real-user Stage 2 acceptance pending until the user verifies the repaired editor")\n    state_text = json.dumps(state, ensure_ascii=False)\n    for stale in ("Exact 0.3.47", "Exact 0.3.48 bytes have not executed", "Exact 0.3.48 runtime remains pending", "Apply the Stage 2 user-acceptance focus repair"):\n        if stale in state_text:\n            return fail(f"SESSION_STATE.json retains stale pre-focus-repair state: {stale}")\n\n'''
    write_lf(path, replace_once(text, anchor, addition, "Stage 2 session-state source verifier"))


def strengthen_material_state_contract(root: pathlib.Path) -> None:
    path = root / "tools/run_python_regression_shards.py"
    text = read_lf(path)
    old = '''        (state.get("stage2_source_implemented") is True, "Stage 2 source implemented"),\n        (state.get("stage2_promotion_blocked") is True, "manual promotion remains blocked"),\n'''
    new = '''        (state.get("stage2_source_implemented") is True and state.get("stage2_runtime_tested") is True and state.get("stage2_windows_ui_tested") is True, "Stage 2 source and automated Windows engineering verification recorded"),\n        (state.get("stage2_promotion_blocked") is True and state.get("stage2_manual_visual_acceptance") is False and state.get("stage2_user_clicked_editor_retest") is False, "manual promotion remains blocked pending real-user editor acceptance"),\n'''
    write_lf(path, replace_once(text, old, new, "Stage 2 material session-state contract"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    args = parser.parse_args()
    root = pathlib.Path(args.source_root).resolve()
    if not (root / "CloudScribe.sln").is_file():
        raise RuntimeError(f"CloudScribe source root is invalid: {root}")

    verify_hashes(root, PREIMAGE_SHA256, "session-state finalization preimage")
    finalize_state(root)
    strengthen_source_verifier(root)
    strengthen_material_state_contract(root)
    verify_hashes(root, POSTIMAGE_SHA256, "session-state finalization postimage")
    print("CLOUDSCRIBE_STAGE2_SESSION_STATE_FINALIZATION=PASS manual_acceptance=pending")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
