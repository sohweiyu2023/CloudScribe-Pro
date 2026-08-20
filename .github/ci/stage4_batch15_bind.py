from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

BATCH14_RUN = 32419643170
BATCH14_COMMIT = "86f9a21b3f051ad4aafb278d65a19bc8ec2fefba"
BATCH14_SOURCE_SHA256 = "79b982d784023ee0becbab720f9acbc3fbef264b3740edab5fbcf077dc10b2d0"
BATCH14_EVIDENCE_ARTIFACT = 9425575109
BATCH14_EVIDENCE_SHA256 = "7b8c692fe604970dd9dcb2eaf59dd29a015c6149f18269c5f7af6b2c1a18decd"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()

    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert state["repository_version"] == "0.5.0-stage4-foundation-batch14"
    assert state["stage4_foundation_batch14"] is True
    assert state["stage4_foundation_batch14_admitted"] is False
    for key in ("stage4_exact_catalog_bytes_available", "stage4_catalog_contract_admitted", "stage4_runtime_policy_exact_bytes_available", "stage4_runtime_policy_contract_admitted", "stage4_limit_taxonomy_exact_bytes_available", "stage4_limit_taxonomy_contract_admitted", "stage4_complete", "stage_gate_passed"):
        assert state[key] is False

    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    state["repository_version"] = "0.5.0-stage4-foundation-batch15"
    state["generated_at_utc"] = generated
    state["status"] = "Stage 3 is promoted. Stage 4 foundation Batches 1-14 are Windows-admitted. Batch 15 binds exact Batch 14 admission evidence before further Stage 4 product changes; unavailable controlling bytes remain fail-closed."
    state["next_exact_action"] = "Windows-admit this Batch 15 evidence-binding checkpoint under exact SDK 10.0.400; then continue only evidence-backed Stage 4 work that does not require unavailable controlling bytes. Do not start Stage 5."
    state["latest_reaudit_completed_at_utc"] = generated
    state["stage4_foundation_batch14_admitted"] = True
    state["stage4_foundation_batch14_admission_run"] = BATCH14_RUN
    state["stage4_foundation_batch14_commit"] = BATCH14_COMMIT
    state["stage4_foundation_batch14_tests"] = "262/262 compiled .NET tests passed; 0 failed; 0 skipped; verifier self-tests including Batch 14 regression and 153/153 deterministic material regressions passed; strict Release/analyzers, native Windows visual/runtime, post-native restore/format/source stability, special-character launcher and deterministic archive/no-mutation gates passed"
    state["stage4_foundation_batch14_source_sha256"] = BATCH14_SOURCE_SHA256
    state["stage4_foundation_batch14_evidence_artifact"] = BATCH14_EVIDENCE_ARTIFACT
    state["stage4_foundation_batch14_evidence_sha256"] = BATCH14_EVIDENCE_SHA256
    state["stage4_foundation_batch15"] = True
    state["stage4_foundation_batch15_admitted"] = False
    state["stage4_batch15_evidence_binding_checkpoint"] = True
    state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    app = root / "src/CloudScribe.App/CloudScribe.App.csproj"
    text = app.read_text(encoding="utf-8-sig")
    old = "<InformationalVersion>0.5.0-stage4-foundation-batch14</InformationalVersion>"
    new = "<InformationalVersion>0.5.0-stage4-foundation-batch15</InformationalVersion>"
    assert text.count(old) == 1
    app.write_text(text.replace(old, new), encoding="utf-8")

    verifier_path = root / "tools/verify_stage4_source.py"
    verifier = verifier_path.read_text(encoding="utf-8-sig")
    anchor = 'BATCH13_EVIDENCE_SHA256 = "72691cb9d090ec83fa51daad7714c02d816f4fb636a855c63e377eb30fcd3ebb"\n'
    addition = anchor + ('BATCH14_COMMIT = "86f9a21b3f051ad4aafb278d65a19bc8ec2fefba"\nBATCH14_RUN = "32419643170"\nBATCH14_SOURCE_SHA256 = "79b982d784023ee0becbab720f9acbc3fbef264b3740edab5fbcf077dc10b2d0"\nBATCH14_EVIDENCE_ARTIFACT = 9425575109\nBATCH14_EVIDENCE_SHA256 = "7b8c692fe604970dd9dcb2eaf59dd29a015c6149f18269c5f7af6b2c1a18decd"\n')
    assert verifier.count(anchor) == 1
    verifier = verifier.replace(anchor, addition)
    old_block = '''    if state.get("stage4_foundation_batch14") is not True or state.get("stage4_foundation_batch14_admitted") is not False:\n        return fail("Current Stage 4 Batch 14 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch14_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 14 must explicitly bind the Batch 13 admission evidence before further source changes")\n'''
    new_block = '''    if state.get("stage4_foundation_batch14") is not True or state.get("stage4_foundation_batch14_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 14 admission")\n    if state.get("stage4_foundation_batch14_commit") != BATCH14_COMMIT or str(state.get("stage4_foundation_batch14_admission_run")) != BATCH14_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 14 Windows admission evidence")\n    if state.get("stage4_foundation_batch14_source_sha256") != BATCH14_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 14 admitted source archive")\n    if state.get("stage4_foundation_batch14_evidence_artifact") != BATCH14_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch14_evidence_sha256") != BATCH14_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 14 evidence artifact")\n    if state.get("stage4_foundation_batch15") is not True or state.get("stage4_foundation_batch15_admitted") is not False:\n        return fail("Current Stage 4 Batch 15 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch15_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 15 must explicitly bind the Batch 14 admission evidence before further source changes")\n'''
    assert verifier.count(old_block) == 1
    verifier = verifier.replace(old_block, new_block)
    verifier = verifier.replace("admitted Batches 1-13 and current unadmitted Batch 14, strict bounded JSON", "admitted Batches 1-14 and current unadmitted Batch 15, strict bounded JSON")
    verifier_path.write_text(verifier, encoding="utf-8")

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig")
    marker = "class Stage4SourceContractTests(unittest.TestCase):\n"
    assert tests.count(marker) == 1
    test = '''class Stage4SourceContractTests(unittest.TestCase):\n    def test_rejects_wrong_batch14_evidence_artifact_binding(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch14-evidence-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_foundation_batch14_evidence_sha256"] = "0" * 64\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("authoritative Batch 14 evidence artifact", result.stderr)\n\n'''
    tests_path.write_text(tests.replace(marker, test, 1), encoding="utf-8")

    runner = root / "tools/run_verifier_self_tests.py"
    runner_text = runner.read_text(encoding="utf-8-sig")
    assert runner_text.count('(\"Stage4SourceContractTests\", 28)') == 1
    runner.write_text(runner_text.replace('(\"Stage4SourceContractTests\", 28)', '(\"Stage4SourceContractTests\", 29)'), encoding="utf-8")

    doc = root / "docs/STAGE4-FOUNDATION-BATCH15.txt"
    assert not doc.exists()
    doc.write_text(f"""CloudScribe Pro — Stage 4 Foundation Batch 15\n\nPurpose\n- Bind exact successful Batch 14 Windows certification evidence before further Stage 4 product-source changes.\n- Preserve fail-closed boundaries for unavailable exact pricing/runtime-policy/limit-taxonomy bytes and production trust.\n\nAuthoritative Batch 14 evidence\n- Run {BATCH14_RUN}\n- Tested head: {BATCH14_COMMIT}\n- 262/262 compiled .NET tests passed; 0 failed; 0 skipped.\n- Portable/verifier/regression, strict Release/analyzers, native Windows visual/runtime, post-native source stability, launcher and final no-mutation gates passed.\n- Deterministic source archive SHA-256: {BATCH14_SOURCE_SHA256}\n- Evidence artifact: {BATCH14_EVIDENCE_ARTIFACT}\n- Downloaded evidence ZIP SHA-256: {BATCH14_EVIDENCE_SHA256}\n\nTruth boundary\n- Exact v2.22 pricing schema/seed bytes remain unavailable and are not imported or reconstructed.\n- Runtime-policy 1.3 and schema-1.1.5 limit-taxonomy exact bytes remain unavailable/unadmitted.\n- No production trust anchor or private signing key is fabricated.\n- This checkpoint does not claim Stage 4 completion/promotion. Stage 5 remains blocked.\n""", encoding="utf-8")
    subprocess.run(["git", "add", "docs/STAGE4-FOUNDATION-BATCH15.txt"], cwd=root, check=True)
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
