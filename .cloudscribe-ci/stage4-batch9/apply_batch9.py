from __future__ import annotations

import hashlib
import json
import subprocess
from pathlib import Path

EXPECTED_PATCH_SHA256 = "4417c10c4c124b30b642aecb6f16ddef8a9ee026de88ea55f7b7d9713dc85a87"
BATCH8_COMMIT = "77fea4e152738eafc00efb78242fa95ec4d56ed3"
BATCH8_RUN = 32094911733
BATCH8_SOURCE_SHA256 = "57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738"
BATCH8_EVIDENCE_ARTIFACT = 9309652831
BATCH8_EVIDENCE_SHA256 = "9dcb5121e43c05d239227bdb169d2f322d2fe023fe578b3d3970e521028bdf00"


def write_lf(path: Path, text: str) -> None:
    path.write_bytes(text.replace("\r\n", "\n").encode("utf-8"))


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if text.count(old) != 1:
        raise SystemExit(f"expected exactly one {label} replacement; found {text.count(old)}")
    return text.replace(old, new, 1)


def run(*args: str, capture: bool = False) -> str:
    result = subprocess.run(args, check=False, text=False, stdout=subprocess.PIPE if capture else None)
    if result.returncode:
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(args)}")
    if not capture:
        return ""
    return result.stdout.decode("utf-8").strip()


def update_state(root: Path) -> None:
    path = root / "SESSION_STATE.json"
    state = json.loads(path.read_text(encoding="utf-8"))
    state["repository_version"] = "0.5.0-stage4-foundation-batch9"
    state["status"] = "Stage 3 is final-Windows-certified and promoted. Stage 4 foundation Batches 1-8 are Windows-admitted. Batch 9 binds the authoritative Batch 8 Ed25519-trust admission evidence and normalizes the live Stage 4 checkpoint while the exact v2.22 production pricing schema/seed and production trust anchor remain unavailable and fail-closed."
    state["next_exact_action"] = "Windows-admit Stage 4 Batch 9 evidence-binding checkpoint under exact SDK 10.0.400; then recover and authenticate the exact v2.22 production pricing schema/seed and intended production trusted public key before Stage 4 promotion. Do not start Stage 5 or fabricate unavailable production bytes."
    state["stage4_foundation_batch8_admitted"] = True
    state["stage4_foundation_batch8_admission_run"] = BATCH8_RUN
    state["stage4_foundation_batch8_commit"] = BATCH8_COMMIT
    state["stage4_foundation_batch8_tests"] = "255/255 compiled .NET tests passed; 72/72 verifier self-tests; 153/153 deterministic regressions; Release build and analyzers passed; locked restore and dotnet format passed"
    state["stage4_foundation_batch8_source_sha256"] = BATCH8_SOURCE_SHA256
    state["stage4_foundation_batch8_evidence_artifact"] = BATCH8_EVIDENCE_ARTIFACT
    state["stage4_foundation_batch8_evidence_sha256"] = BATCH8_EVIDENCE_SHA256
    state["stage4_foundation_batch9"] = True
    state["stage4_foundation_batch9_admitted"] = False
    state["stage4_batch9_evidence_binding_checkpoint"] = True
    write_lf(path, json.dumps(state, indent=2) + "\n")


def write_doc(root: Path) -> None:
    text = """CloudScribe Pro — Stage 4 Foundation Batch 9

Purpose
- Bind the authoritative Windows admission evidence for Batch 8 and normalize the live Stage 4 checkpoint before any later production-catalog gate.

Authoritative Batch 8 evidence
- Admission run: 32094911733.
- Admitted commit: 77fea4e152738eafc00efb78242fa95ec4d56ed3.
- Compiled tests: 255/255 passed, 0 failed, 0 skipped.
- Windows verifier self-tests: 72/72 passed.
- Deterministic regressions: 153/153 passed.
- Evidence artifact: 9309652831.
- Evidence artifact SHA-256: 9dcb5121e43c05d239227bdb169d2f322d2fe023fe578b3d3970e521028bdf00.
- Deterministic admitted source ZIP SHA-256: 57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738.

Checkpoint normalization
- Stage 4 foundation Batches 1-8 are now recorded as Windows-admitted.
- Batch 9 is itself a source-changing evidence-binding candidate and must remain not admitted until its own Windows run passes.
- Real Ed25519 verification remains external-key-only and the shipped trusted-key map remains empty.

Remaining Stage 4 blocker
- The exact v2.22 production pricing schema/seed bytes are still unavailable in the active source package.
- The intended production trusted public key is not shipped or inferred.
- Do not fabricate pricing bytes, trust anchors, admission evidence, or a Stage 4 promotion.
- Do not begin Stage 5 until the Stage 4 promotion gate is genuinely complete.
"""
    write_lf(root / "docs/STAGE4-FOUNDATION-BATCH9.txt", text)


def update_verifier(root: Path) -> None:
    path = root / "tools/verify_stage4_source.py"
    text = path.read_text(encoding="utf-8")
    text = replace_once(text, 'BATCH7_RUN = "32053498219"\n', 'BATCH7_RUN = "32053498219"\nBATCH8_COMMIT = "77fea4e152738eafc00efb78242fa95ec4d56ed3"\nBATCH8_RUN = "32094911733"\nBATCH8_SOURCE_SHA256 = "57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738"\nBATCH8_EVIDENCE_ARTIFACT = 9309652831\nBATCH8_EVIDENCE_SHA256 = "9dcb5121e43c05d239227bdb169d2f322d2fe023fe578b3d3970e521028bdf00"\n', "Batch 8 evidence constants")
    text = replace_once(text, '    if state.get("stage4_foundation_batch8") is not True or state.get("stage4_foundation_batch8_admitted") is not False:\n        return fail("Current Stage 4 Batch 8 must remain a source-changing candidate until Windows admission")\n', '    if state.get("stage4_foundation_batch8") is not True or state.get("stage4_foundation_batch8_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 8 admission")\n    if state.get("stage4_foundation_batch8_commit") != BATCH8_COMMIT or str(state.get("stage4_foundation_batch8_admission_run")) != BATCH8_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 8 Windows admission evidence")\n    if state.get("stage4_foundation_batch8_source_sha256") != BATCH8_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 8 admitted source archive")\n    if state.get("stage4_foundation_batch8_evidence_artifact") != BATCH8_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch8_evidence_sha256") != BATCH8_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 8 evidence artifact")\n    if state.get("stage4_foundation_batch9") is not True or state.get("stage4_foundation_batch9_admitted") is not False or state.get("stage4_batch9_evidence_binding_checkpoint") is not True:\n        return fail("Current Stage 4 Batch 9 must remain a source-changing evidence-binding candidate until Windows admission")\n', "Batch 8/9 state contract")
    anchor = '        require_text(root, "docs/STAGE4-FOUNDATION-BATCH8.txt",\n            "real Ed25519 verification", "shipped trusted-key mapping empty", "does not contain private signing key material")\n'
    addition = anchor + '        require_text(root, "docs/STAGE4-FOUNDATION-BATCH9.txt",\n            "Admission run: 32094911733", "255/255 passed", "Evidence artifact: 9309652831",\n            "57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738",\n            "Do not begin Stage 5")\n'
    text = replace_once(text, anchor, addition, "Batch 9 documentation contract")
    text = replace_once(text, "preserves promoted Stage 3 lineage and admitted Batches 1-7,", "preserves promoted Stage 3 lineage and admitted Batches 1-8,", "Stage 4 success message")
    write_lf(path, text)


def update_tests(root: Path) -> None:
    path = root / "tests/test_verification_tools.py"
    text = path.read_text(encoding="utf-8")
    marker = '    def test_rejects_built_in_pricing_trust_key(self):\n'
    insert = '''    def test_rejects_false_batch8_admission_state(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch8-admission-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch8_admitted"] = False
            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative successful Batch 8 admission", result.stderr)

    def test_rejects_wrong_batch8_admission_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch8-binding-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch8_commit"] = "0" * 40
            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 8 Windows admission evidence", result.stderr)

    def test_rejects_wrong_batch8_evidence_artifact_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch8-artifact-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch8_evidence_sha256"] = "0" * 64
            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 8 evidence artifact", result.stderr)

'''
    text = replace_once(text, marker, insert + marker, "Batch 8 evidence mutation tests")
    write_lf(path, text)
    count_path = root / "tools/run_verifier_self_tests.py"
    count_text = count_path.read_text(encoding="utf-8")
    count_text = replace_once(count_text, '("Stage4SourceContractTests", 17)', '("Stage4SourceContractTests", 20)', "Stage 4 verifier test count")
    write_lf(count_path, count_text)


def main() -> None:
    root = Path.cwd()
    update_state(root)
    write_doc(root)
    update_verifier(root)
    update_tests(root)
    run("python", "tools/update_sha256_manifest.py")
    run("python", "tools/update_sha256_manifest.py", "--check")
    changed = sorted(filter(None, run("git", "status", "--porcelain", capture=True).splitlines()))
    if len(changed) != 6:
        raise SystemExit(f"expected 6 changed paths after Batch 9 transform; found {len(changed)}: {changed}")
    patch = subprocess.check_output(["git", "diff", "--binary", "--full-index"])
    digest = hashlib.sha256(patch).hexdigest()
    if digest != EXPECTED_PATCH_SHA256:
        raise SystemExit(f"Batch 9 generated patch SHA mismatch: {digest}")
    print(f"CLOUDSCRIBE_STAGE4_BATCH9_TRANSFORMED files=6 sha256={digest}")


if __name__ == "__main__":
    main()
