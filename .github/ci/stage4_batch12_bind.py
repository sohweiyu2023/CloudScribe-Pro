from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()

    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert state["repository_version"] == "0.5.0-stage4-foundation-batch11"
    assert state["stage4_foundation_batch11"] is True
    assert state["stage4_foundation_batch11_admitted"] is False
    assert state["stage4_exact_catalog_bytes_available"] is False
    assert state["stage4_catalog_contract_admitted"] is False
    assert state["stage4_runtime_policy_exact_bytes_available"] is False
    assert state["stage4_runtime_policy_contract_admitted"] is False
    assert state["stage4_limit_taxonomy_exact_bytes_available"] is False
    assert state["stage4_limit_taxonomy_contract_admitted"] is False

    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    state["repository_version"] = "0.5.0-stage4-foundation-batch12"
    state["generated_at_utc"] = generated
    state["status"] = (
        "Stage 3 is final-Windows-certified and promoted. Stage 4 foundation Batches 1-11 are Windows-admitted. "
        "Batch 12 binds the exact Batch 11 admission evidence and authenticated v2.22 control identities while "
        "keeping unavailable exact pricing schema/seed, runtime-policy, limit-taxonomy and production trust bytes fail-closed."
    )
    state["next_exact_action"] = (
        "Windows-admit this Batch 12 evidence-binding checkpoint under exact SDK 10.0.400; then import only authenticated "
        "exact v2.22 pricing schema/seed bytes and prove Draft 2020-12 plus supplied semantic-validator agreement before "
        "catalog-contract admission. Runtime-policy 1.3 and schema-1.1.5 limit taxonomy remain separately gated. Do not start Stage 5."
    )
    state["latest_reaudit_completed_at_utc"] = generated
    state["stage4_foundation_batch11_admitted"] = True
    state["stage4_foundation_batch11_admission_run"] = 32387286642
    state["stage4_foundation_batch11_commit"] = "801952c69b17ab38d7bacb527f9de1401076bc2a"
    state["stage4_foundation_batch11_merge_commit"] = "34ea9435c72ce3229afc5d52cd6851d3a4d43078"
    state["stage4_foundation_batch11_tests"] = (
        "262/262 compiled .NET tests passed; 0 failed; 0 skipped; full native Windows, formatter, launcher, "
        "source-stability and deterministic archive gates passed"
    )
    state["stage4_foundation_batch11_source_sha256"] = "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174"
    state["stage4_foundation_batch11_evidence_artifact"] = 9413736543
    state["stage4_foundation_batch11_evidence_sha256"] = "5b91ba0b6ed72f7be433308d3faa19655ceec3bab67a751bee4329e3e3965124"
    state["stage4_foundation_batch12"] = True
    state["stage4_foundation_batch12_admitted"] = False
    state["stage4_batch12_evidence_binding_checkpoint"] = True
    state["controlling_package_expected_sha256"] = "22b0609ca1375488ac04c8a807cfb08ad34a08aa883a8dc2984516e64f68f8b3"
    state["stage4_pricing_schema_expected_sha256"] = "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b"
    state["stage4_pricing_seed_expected_sha256"] = "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61"
    state["stage4_runtime_policy_schema_expected_sha256"] = "bdcc03005a48d9d8bdcb139d468a9c3f277526aa4d9dbe19c2c6309b5bff390c"
    state["stage4_runtime_policy_seed_expected_sha256"] = "9561a4f5c1d58dd471424566b05f7325a52ed06a4c57ec53b17f5395ae621525"
    state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    app_project = root / "src/CloudScribe.App/CloudScribe.App.csproj"
    app_text = app_project.read_text(encoding="utf-8-sig")
    old = "<InformationalVersion>0.5.0-stage4-foundation-batch11</InformationalVersion>"
    new = "<InformationalVersion>0.5.0-stage4-foundation-batch12</InformationalVersion>"
    assert app_text.count(old) == 1
    app_project.write_text(app_text.replace(old, new), encoding="utf-8")

    verifier_path = root / "tools/verify_stage4_source.py"
    verifier = verifier_path.read_text(encoding="utf-8-sig")
    anchor = 'BATCH10_EVIDENCE_SHA256 = "75bf495e893e8d11ad44b5d0b97fcf948e7939fa83702f6a07066e13b8951533"\n'
    addition = anchor + (
        'BATCH11_COMMIT = "801952c69b17ab38d7bacb527f9de1401076bc2a"\n'
        'BATCH11_MERGE_COMMIT = "34ea9435c72ce3229afc5d52cd6851d3a4d43078"\n'
        'BATCH11_RUN = "32387286642"\n'
        'BATCH11_SOURCE_SHA256 = "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174"\n'
        'BATCH11_EVIDENCE_ARTIFACT = 9413736543\n'
        'BATCH11_EVIDENCE_SHA256 = "5b91ba0b6ed72f7be433308d3faa19655ceec3bab67a751bee4329e3e3965124"\n'
        'V222_PACKAGE_SHA256 = "22b0609ca1375488ac04c8a807cfb08ad34a08aa883a8dc2984516e64f68f8b3"\n'
        'V222_PRICING_SCHEMA_SHA256 = "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b"\n'
        'V222_PRICING_SEED_SHA256 = "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61"\n'
    )
    assert verifier.count(anchor) == 1
    verifier = verifier.replace(anchor, addition)
    old_block = '''    if state.get("stage4_foundation_batch11") is not True or state.get("stage4_foundation_batch11_admitted") is not False:\n        return fail("Current Stage 4 Batch 11 must remain a source-changing candidate until Windows admission")\n'''
    new_block = '''    if state.get("stage4_foundation_batch11") is not True or state.get("stage4_foundation_batch11_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 11 admission")\n    if state.get("stage4_foundation_batch11_commit") != BATCH11_COMMIT or str(state.get("stage4_foundation_batch11_admission_run")) != BATCH11_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 11 Windows admission evidence")\n    if state.get("stage4_foundation_batch11_merge_commit") != BATCH11_MERGE_COMMIT:\n        return fail("Stage 4 is not bound to the authoritative Batch 11 merge checkpoint")\n    if state.get("stage4_foundation_batch11_source_sha256") != BATCH11_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 11 admitted source archive")\n    if state.get("stage4_foundation_batch11_evidence_artifact") != BATCH11_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch11_evidence_sha256") != BATCH11_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 11 evidence artifact")\n    if state.get("stage4_foundation_batch12") is not True or state.get("stage4_foundation_batch12_admitted") is not False:\n        return fail("Current Stage 4 Batch 12 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_batch12_evidence_binding_checkpoint") is not True:\n        return fail("Stage 4 Batch 12 must explicitly bind the prior admission evidence before further source changes")\n    if state.get("controlling_package_expected_sha256") != V222_PACKAGE_SHA256 or state.get("stage4_pricing_schema_expected_sha256") != V222_PRICING_SCHEMA_SHA256 or state.get("stage4_pricing_seed_expected_sha256") != V222_PRICING_SEED_SHA256:\n        return fail("Stage 4 Batch 12 is not bound to the authenticated v2.22 pricing control identities")\n'''
    assert verifier.count(old_block) == 1
    verifier = verifier.replace(old_block, new_block)
    doc_anchor = '''        require_text(root, "docs/STAGE4-FOUNDATION-BATCH11.txt",\n            "plans are required alongside meters", "Admission run: 32113025375",\n            "257/257 passed", "Stage 4 completion or promotion", "Stage 5 start")\n'''
    doc_addition = doc_anchor + '''        require_text(root, "docs/STAGE4-FOUNDATION-BATCH12.txt",\n            "Run 32387286642", "262/262", "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174",\n            "exact v2.22 pricing schema/seed bytes are not yet imported", "Stage 5 remains blocked")\n'''
    assert verifier.count(doc_anchor) == 1
    verifier = verifier.replace(doc_anchor, doc_addition)
    verifier = verifier.replace(
        "admitted Batches 1-10, strict bounded JSON",
        "admitted Batches 1-11 and current unadmitted Batch 12, strict bounded JSON",
    )
    verifier_path.write_text(verifier, encoding="utf-8")

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig")
    test_anchor = '''    def test_rejects_false_exact_catalog_admission_claim(self):\n'''
    test_method = '''    def test_rejects_false_batch12_admission_state(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch12-admission-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_foundation_batch12_admitted"] = True\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("Batch 12 must remain a source-changing candidate", result.stderr)\n\n'''
    assert tests.count(test_anchor) == 1
    tests_path.write_text(tests.replace(test_anchor, test_method + test_anchor), encoding="utf-8")

    selftest_path = root / "tools/run_verifier_self_tests.py"
    selftests = selftest_path.read_text(encoding="utf-8-sig")
    old_count = '("Stage4SourceContractTests", 26),'
    new_count = '("Stage4SourceContractTests", 27),'
    assert selftests.count(old_count) == 1
    selftest_path.write_text(selftests.replace(old_count, new_count), encoding="utf-8")

    doc = root / "docs/STAGE4-FOUNDATION-BATCH12.txt"
    assert not doc.exists()
    doc.write_text(
        """CloudScribe Pro — Stage 4 Foundation Batch 12

Purpose
- Bind the exact admitted Batch 11 Windows evidence before any further Stage 4 product changes.
- Lock authenticated v2.22 package/schema/seed identities without pretending hash knowledge equals possession of exact bytes.
- Preserve fail-closed catalog/runtime-policy/limit-taxonomy seams until their exact bytes are imported and independently validated.

Authoritative Batch 11 evidence
- Run 32387286642
- Tested head: 801952c69b17ab38d7bacb527f9de1401076bc2a
- Merge checkpoint: 34ea9435c72ce3229afc5d52cd6851d3a4d43078
- 262/262 compiled .NET tests passed; 0 failed; 0 skipped.
- Deterministic admitted source archive SHA-256: 50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174
- Evidence artifact: 9413736543
- Evidence artifact SHA-256: 5b91ba0b6ed72f7be433308d3faa19655ceec3bab67a751bee4329e3e3965124

Exact-control truth boundary
- Controlling v2.22 package expected SHA-256: 22b0609ca1375488ac04c8a807cfb08ad34a08aa883a8dc2984516e64f68f8b3
- Pricing schema 1.1.5 expected SHA-256: 1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b
- Dated pricing seed expected SHA-256: 3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61
- The exact v2.22 pricing schema/seed bytes are not yet imported into this product source checkpoint.
- Runtime-policy 1.3 and schema-1.1.5 limit-taxonomy bytes remain separately unadmitted.
- No production trusted Ed25519 key is invented or embedded; trusted keys remain external-only.

Non-claims
- This Batch 12 candidate does not claim catalog-contract admission, current production pricing, Stage 4 completion or promotion.
- Stage 5 remains blocked until Stage 4 is fully promoted.
""",
        encoding="utf-8",
    )
    # The generated document is an intended Batch-12 source file, not a build byproduct.
    # Stage it immediately so the pre-freeze untracked-file guard can remain strict.
    subprocess.run(
        ["git", "add", "docs/STAGE4-FOUNDATION-BATCH12.txt"],
        cwd=root,
        check=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
