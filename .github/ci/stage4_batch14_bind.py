from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

BATCH13_RUN = 32408857066
BATCH13_COMMIT = "2f3761802f58f824c70d824087cf027f03697d38"
BATCH13_SOURCE_SHA256 = "e2b2956dd9e6c4fcb6bcdc9e3a2a6fe64adda119f3e512db756a2267437ffe9d"
BATCH13_EVIDENCE_ARTIFACT = 9421654212
BATCH13_EVIDENCE_SHA256 = "72691cb9d090ec83fa51daad7714c02d816f4fb636a855c63e377eb30fcd3ebb"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()

    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert state["repository_version"] == "0.5.0-stage4-foundation-batch13"
    assert state["stage4_foundation_batch13"] is True
    assert state["stage4_foundation_batch13_admitted"] is False
    assert state["stage4_exact_catalog_bytes_available"] is False
    assert state["stage4_catalog_contract_admitted"] is False
    assert state["stage4_runtime_policy_exact_bytes_available"] is False
    assert state["stage4_runtime_policy_contract_admitted"] is False
    assert state["stage4_limit_taxonomy_exact_bytes_available"] is False
    assert state["stage4_limit_taxonomy_contract_admitted"] is False
    assert state["stage4_complete"] is False
    assert state["stage_gate_passed"] is False

    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    state["repository_version"] = "0.5.0-stage4-foundation-batch14"
    state["generated_at_utc"] = generated
    state["status"] = "Stage 3 is promoted. Stage 4 foundation Batches 1-13 are Windows-admitted. Batch 14 binds exact Batch 13 admission evidence before further Stage 4 product changes; unavailable controlling bytes remain fail-closed."
    state["next_exact_action"] = "Windows-admit this Batch 14 evidence-binding checkpoint under exact SDK 10.0.400; then continue only evidence-backed Stage 4 work that does not require unavailable controlling bytes. Do not start Stage 5."
    state["latest_reaudit_completed_at_utc"] = generated
    state["stage4_foundation_batch13_admitted"] = True
    state["stage4_foundation_batch13_admission_run"] = BATCH13_RUN
    state["stage4_foundation_batch13_commit"] = BATCH13_COMMIT
    state["stage4_foundation_batch13_tests"] = "262/262 compiled .NET tests passed; 0 failed; 0 skipped; 82/82 auxiliary Python verifier self-tests; 153/153 deterministic material regressions; strict Release/analyzers, native Windows visual/runtime, post-native restore/format/source stability, special-character launcher and deterministic archive/no-mutation gates passed"
    state["stage4_foundation_batch13_source_sha256"] = BATCH13_SOURCE_SHA256
    state["stage4_foundation_batch13_evidence_artifact"] = BATCH13_EVIDENCE_ARTIFACT
    state["stage4_foundation_batch13_evidence_sha256"] = BATCH13_EVIDENCE_SHA256
    state["stage4_foundation_batch14"] = True
    state["stage4_foundation_batch14_admitted"] = False
    state["stage4_batch14_evidence_binding_checkpoint"] = True
    state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    app_project = root / "src/CloudScribe.App/CloudScribe.App.csproj"
    app_text = app_project.read_text(encoding="utf-8-sig")
    old = "<InformationalVersion>0.5.0-stage4-foundation-batch13</InformationalVersion>"
    new = "<InformationalVersion>0.5.0-stage4-foundation-batch14</InformationalVersion>"
    assert app_text.count(old) == 1
    app_project.write_text(app_text.replace(old, new), encoding="utf-8")

    verifier_path = root / "tools/verify_stage4_source.py"
    verifier = verifier_path.read_text(encoding="utf-8-sig")
    anchor = 'BATCH12_EVIDENCE_SHA256 = "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607"\n'
    addition = anchor + ('BATCH13_COMMIT = "2f3761802f58f824c70d824087cf027f03697d38"\nBATCH13_RUN = "32408857066"\nBATCH13_SOURCE_SHA256 = "e2b2956dd9e6c4fcb6bcdc9e3a2a6fe64adda119f3e512db756a2267437ffe9d"\nBATCH13_EVIDENCE_ARTIFACT = 9421654212\nBATCH13_EVIDENCE_SHA256 = "72691cb9d090ec83fa51daad7714c02d816f4fb636a855c63e377eb30fcd3ebb"\n')
    assert verifier.count(anchor) == 1
    verifier = verifier.replace(anchor, addition)

    old_block = '''    if state.get("stage4_foundation_batch13") is not True or state.get("stage4_foundation_batch13_admitted") is not False:\n        return fail("Current Stage 4 Batch 13 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch13_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 13 must explicitly bind the Batch 12 admission evidence before further source changes")\n'''
    new_block = '''    if state.get("stage4_foundation_batch13") is not True or state.get("stage4_foundation_batch13_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 13 admission")\n    if state.get("stage4_foundation_batch13_commit") != BATCH13_COMMIT or str(state.get("stage4_foundation_batch13_admission_run")) != BATCH13_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 13 Windows admission evidence")\n    if state.get("stage4_foundation_batch13_source_sha256") != BATCH13_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 13 admitted source archive")\n    if state.get("stage4_foundation_batch13_evidence_artifact") != BATCH13_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch13_evidence_sha256") != BATCH13_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 13 evidence artifact")\n    if state.get("stage4_foundation_batch14") is not True or state.get("stage4_foundation_batch14_admitted") is not False:\n        return fail("Current Stage 4 Batch 14 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch14_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 14 must explicitly bind the Batch 13 admission evidence before further source changes")\n'''
    assert verifier.count(old_block) == 1
    verifier = verifier.replace(old_block, new_block)
    verifier = verifier.replace("admitted Batches 1-12 and current unadmitted Batch 13, strict bounded JSON", "admitted Batches 1-13 and current unadmitted Batch 14, strict bounded JSON")
    verifier_path.write_text(verifier, encoding="utf-8")

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig")
    marker = "class Stage4SourceContractTests(unittest.TestCase):\n"
    assert tests.count(marker) == 1
    test = '''class Stage4SourceContractTests(unittest.TestCase):\n    def test_rejects_wrong_batch13_evidence_artifact_binding(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch13-evidence-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_foundation_batch13_evidence_sha256"] = "0" * 64\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("authoritative Batch 13 evidence artifact", result.stderr)\n\n'''
    tests_path.write_text(tests.replace(marker, test, 1), encoding="utf-8")

    doc = root / "docs/STAGE4-FOUNDATION-BATCH14.txt"
    assert not doc.exists()
    doc.write_text("""CloudScribe Pro — Stage 4 Foundation Batch 14\n\nPurpose\n- Bind exact successful Batch 13 Windows certification evidence before further Stage 4 product-source changes.\n- Preserve fail-closed boundaries for unavailable exact pricing/runtime-policy/limit-taxonomy bytes and production trust.\n\nAuthoritative Batch 13 evidence\n- Run 32408857066\n- Tested head: 2f3761802f58f824c70d824087cf027f03697d38\n- 262/262 compiled .NET tests passed; 0 failed; 0 skipped.\n- 82/82 auxiliary Python verifier self-tests and 153/153 deterministic material regressions passed.\n- Strict Release/analyzers, native Windows visual/runtime, post-native restore/format/source stability, special-character launcher and final no-mutation guard passed.\n- Deterministic source archive SHA-256: e2b2956dd9e6c4fcb6bcdc9e3a2a6fe64adda119f3e512db756a2267437ffe9d\n- Evidence artifact: 9421654212\n- Evidence artifact ZIP SHA-256: 72691cb9d090ec83fa51daad7714c02d816f4fb636a855c63e377eb30fcd3ebb\n\nTruth boundary\n- Exact v2.22 pricing schema/seed bytes remain unavailable and are not imported or reconstructed.\n- Runtime-policy 1.3 and schema-1.1.5 limit-taxonomy exact bytes remain unavailable/unadmitted.\n- No production trust anchor or private signing key is fabricated.\n- This checkpoint does not claim Stage 4 completion/promotion. Stage 5 remains blocked.\n""", encoding="utf-8")
    subprocess.run(["git", "add", "docs/STAGE4-FOUNDATION-BATCH14.txt"], cwd=root, check=True)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
