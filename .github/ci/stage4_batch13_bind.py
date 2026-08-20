from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

BATCH12_RUN = 32398889342
BATCH12_COMMIT = "eb7236d72765377804b9c8b7131ff4d26d7d6357"
BATCH12_SOURCE_SHA256 = "2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889"
BATCH12_EVIDENCE_ARTIFACT = 9418053746
BATCH12_EVIDENCE_SHA256 = "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()

    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert state["repository_version"] == "0.5.0-stage4-foundation-batch12"
    assert state["stage4_foundation_batch12"] is True
    assert state["stage4_foundation_batch12_admitted"] is False
    assert state["stage4_exact_catalog_bytes_available"] is False
    assert state["stage4_catalog_contract_admitted"] is False
    assert state["stage4_runtime_policy_exact_bytes_available"] is False
    assert state["stage4_runtime_policy_contract_admitted"] is False
    assert state["stage4_limit_taxonomy_exact_bytes_available"] is False
    assert state["stage4_limit_taxonomy_contract_admitted"] is False
    assert state["stage4_complete"] is False
    assert state["stage_gate_passed"] is False

    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    state["repository_version"] = "0.5.0-stage4-foundation-batch13"
    state["generated_at_utc"] = generated
    state["status"] = (
        "Stage 3 is final-Windows-certified and promoted. Stage 4 foundation Batches 1-12 are Windows-admitted. "
        "Batch 13 binds the exact Batch 12 admission evidence before further Stage 4 product changes while "
        "keeping unavailable exact pricing schema/seed, runtime-policy, limit-taxonomy and production trust bytes fail-closed."
    )
    state["next_exact_action"] = (
        "Windows-admit this Batch 13 evidence-binding checkpoint under exact SDK 10.0.400; then continue only evidence-backed "
        "Stage 4 work that does not require unavailable controlling bytes. Import the pricing schema/seed only when exact v2.22 bytes "
        "are authenticated and Draft 2020-12 plus supplied semantic-validator agreement is demonstrated. Do not start Stage 5."
    )
    state["latest_reaudit_completed_at_utc"] = generated
    state["stage4_foundation_batch12_admitted"] = True
    state["stage4_foundation_batch12_admission_run"] = BATCH12_RUN
    state["stage4_foundation_batch12_commit"] = BATCH12_COMMIT
    state["stage4_foundation_batch12_tests"] = (
        "262/262 compiled .NET tests passed; 0 failed; 0 skipped; 82/82 auxiliary Python verifier self-tests; "
        "153/153 deterministic material regressions; strict Release build/analyzers 0 warnings/0 errors; native Windows visual/runtime, "
        "post-native restore/format/source-stability, special-character launcher and deterministic archive/no-mutation gates passed"
    )
    state["stage4_foundation_batch12_source_sha256"] = BATCH12_SOURCE_SHA256
    state["stage4_foundation_batch12_evidence_artifact"] = BATCH12_EVIDENCE_ARTIFACT
    state["stage4_foundation_batch12_evidence_sha256"] = BATCH12_EVIDENCE_SHA256
    state["stage4_foundation_batch13"] = True
    state["stage4_foundation_batch13_admitted"] = False
    state["stage4_batch13_evidence_binding_checkpoint"] = True
    state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    app_project = root / "src/CloudScribe.App/CloudScribe.App.csproj"
    app_text = app_project.read_text(encoding="utf-8-sig")
    old = "<InformationalVersion>0.5.0-stage4-foundation-batch12</InformationalVersion>"
    new = "<InformationalVersion>0.5.0-stage4-foundation-batch13</InformationalVersion>"
    assert app_text.count(old) == 1
    app_project.write_text(app_text.replace(old, new), encoding="utf-8")

    verifier_path = root / "tools/verify_stage4_source.py"
    verifier = verifier_path.read_text(encoding="utf-8-sig")
    anchor = 'V222_PRICING_SEED_SHA256 = "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61"\n'
    addition = anchor + (
        'BATCH12_COMMIT = "eb7236d72765377804b9c8b7131ff4d26d7d6357"\n'
        'BATCH12_RUN = "32398889342"\n'
        'BATCH12_SOURCE_SHA256 = "2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889"\n'
        'BATCH12_EVIDENCE_ARTIFACT = 9418053746\n'
        'BATCH12_EVIDENCE_SHA256 = "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607"\n'
    )
    assert verifier.count(anchor) == 1
    verifier = verifier.replace(anchor, addition)

    old_block = '''    if state.get("stage4_foundation_batch12") is not True or state.get("stage4_foundation_batch12_admitted") is not False:\n        return fail("Current Stage 4 Batch 12 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch12_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 12 must explicitly bind the prior admission evidence before further source changes")\n    if state.get("controlling_package_expected_sha256") != V222_PACKAGE_SHA256 or state.get("stage4_pricing_schema_expected_sha256") != V222_PRICING_SCHEMA_SHA256 or state.get("stage4_pricing_seed_expected_sha256") != V222_PRICING_SEED_SHA256:\n        return fail("Stage 4 Batch 12 is not bound to the authenticated v2.22 pricing control identities")\n'''
    new_block = '''    if state.get("stage4_foundation_batch12") is not True or state.get("stage4_foundation_batch12_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 12 admission")\n    if state.get("stage4_foundation_batch12_commit") != BATCH12_COMMIT or str(state.get("stage4_foundation_batch12_admission_run")) != BATCH12_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 12 Windows admission evidence")\n    if state.get("stage4_foundation_batch12_source_sha256") != BATCH12_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 12 admitted source archive")\n    if state.get("stage4_foundation_batch12_evidence_artifact") != BATCH12_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch12_evidence_sha256") != BATCH12_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 12 evidence artifact")\n    if state.get("stage4_foundation_batch13") is not True or state.get("stage4_foundation_batch13_admitted") is not False:\n        return fail("Current Stage 4 Batch 13 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch13_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 13 must explicitly bind the Batch 12 admission evidence before further source changes")\n    if state.get("controlling_package_expected_sha256") != V222_PACKAGE_SHA256 or state.get("stage4_pricing_schema_expected_sha256") != V222_PRICING_SCHEMA_SHA256 or state.get("stage4_pricing_seed_expected_sha256") != V222_PRICING_SEED_SHA256:\n        return fail("Stage 4 remains unbound from the authenticated v2.22 pricing control identities")\n'''
    assert verifier.count(old_block) == 1
    verifier = verifier.replace(old_block, new_block)

    doc_anchor = '''        require_text(root, "docs/STAGE4-FOUNDATION-BATCH12.txt",\n            "Run 32387286642", "262/262", "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174",\n            "exact v2.22 pricing schema/seed bytes are not yet imported", "Stage 5 remains blocked")\n'''
    doc_addition = doc_anchor + '''        require_text(root, "docs/STAGE4-FOUNDATION-BATCH13.txt",\n            "Run 32398889342", "262/262", "82/82", "153/153",\n            "2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889",\n            "9418053746", "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607",\n            "exact v2.22 pricing schema/seed bytes remain unavailable", "Stage 5 remains blocked")\n'''
    assert verifier.count(doc_anchor) == 1
    verifier = verifier.replace(doc_anchor, doc_addition)
    verifier = verifier.replace(
        "admitted Batches 1-11 and current unadmitted Batch 12, strict bounded JSON",
        "admitted Batches 1-12 and current unadmitted Batch 13, strict bounded JSON",
    )
    verifier_path.write_text(verifier, encoding="utf-8")

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig")
    old_test = '''    def test_rejects_false_batch12_admission_state(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch12-admission-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_foundation_batch12_admitted"] = True\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("Batch 12 must remain a source-changing candidate", result.stderr)\n\n'''
    new_test = '''    def test_rejects_wrong_batch12_evidence_artifact_binding(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch12-evidence-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_foundation_batch12_evidence_sha256"] = "0" * 64\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("authoritative Batch 12 evidence artifact", result.stderr)\n\n'''
    assert tests.count(old_test) == 1
    tests_path.write_text(tests.replace(old_test, new_test), encoding="utf-8")

    doc = root / "docs/STAGE4-FOUNDATION-BATCH13.txt"
    assert not doc.exists()
    doc.write_text(
        """CloudScribe Pro — Stage 4 Foundation Batch 13

Purpose
- Bind the exact successful Batch 12 Windows certification evidence before any further Stage 4 product-source changes.
- Preserve the authenticated v2.22 control identity locks without confusing hashes with possession or validation of exact controlling bytes.
- Keep pricing-catalog, runtime-policy, limit-taxonomy and production trust boundaries fail-closed.

Authoritative Batch 12 evidence
- Run 32398889342
- Tested head: eb7236d72765377804b9c8b7131ff4d26d7d6357
- 262/262 compiled .NET tests passed; 0 failed; 0 skipped.
- 82/82 auxiliary Python verifier self-tests passed on Windows.
- 153/153 deterministic material regression checks passed.
- Strict Release build/analyzers passed with 0 warnings and 0 errors.
- Native Windows visual/runtime certification, post-native locked restore/format/source stability, special-character launcher regression and final no-mutation guard passed.
- Deterministic source archive SHA-256: 2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889
- Evidence artifact: 9418053746
- Evidence artifact ZIP SHA-256: 01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607

Truth boundary
- The exact v2.22 pricing schema/seed bytes remain unavailable and are not imported or reconstructed.
- Hash identity locks do not constitute schema possession, Draft 2020-12 validation, or supplied semantic-validator agreement.
- Runtime-policy 1.3 and schema-1.1.5 limit-taxonomy exact bytes remain separately unavailable/unadmitted.
- No production trusted Ed25519 public key or private signing key is fabricated or embedded.

Non-claims
- This Batch 13 candidate does not claim catalog-contract admission, current production pricing, Stage 4 completion or promotion.
- Stage 5 remains blocked until Stage 4 is fully promoted.
""",
        encoding="utf-8",
    )
    subprocess.run(["git", "add", "docs/STAGE4-FOUNDATION-BATCH13.txt"], cwd=root, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
