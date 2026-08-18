#!/usr/bin/env python3
from __future__ import annotations
import json
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path.cwd().resolve()


def write(relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


write("src/CloudScribe.Domain/Pricing/PricingPlanDefinition.cs", '''namespace CloudScribe.Domain.Pricing;\n\npublic sealed record PricingPlanDefinition\n{\n    public PricingPlanDefinition(\n        string stableId,\n        IReadOnlyList<string> meterStableIds,\n        string provenanceId)\n    {\n        StableId = PricingMeterDefinition.NormalizeStableToken(stableId, nameof(stableId));\n        ArgumentNullException.ThrowIfNull(meterStableIds);\n        if (meterStableIds.Count == 0)\n        {\n            throw new ArgumentException("A pricing plan requires at least one meter reference.", nameof(meterStableIds));\n        }\n\n        var seen = new HashSet<string>(StringComparer.Ordinal);\n        var copiedMeterStableIds = new string[meterStableIds.Count];\n        for (int index = 0; index < meterStableIds.Count; index++)\n        {\n            string meterStableId = PricingMeterDefinition.NormalizeStableToken(\n                meterStableIds[index],\n                nameof(meterStableIds));\n            if (!seen.Add(meterStableId))\n            {\n                throw new ArgumentException(\n                    "Pricing plan meter references must be unique.",\n                    nameof(meterStableIds));\n            }\n\n            copiedMeterStableIds[index] = meterStableId;\n        }\n\n        MeterStableIds = copiedMeterStableIds;\n        ProvenanceId = NormalizeProvenance(provenanceId);\n    }\n\n    public string StableId { get; }\n    public IReadOnlyList<string> MeterStableIds { get; }\n    public string ProvenanceId { get; }\n\n    private static string NormalizeProvenance(string value)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(value);\n        string normalized = value.Trim();\n        if (normalized.Length > 160\n            || normalized.Any(static character =>\n                char.IsControl(character)\n                || char.IsSurrogate(character)\n                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))\n        {\n            throw new ArgumentException(\n                "Pricing plan provenance is limited to 160 visible characters.",\n                nameof(value));\n        }\n\n        return normalized;\n    }\n}\n''')

write("tests/CloudScribe.Domain.Tests/PricingPlanDefinitionTests.cs", '''using CloudScribe.Domain.Pricing;\n\nnamespace CloudScribe.Domain.Tests;\n\npublic sealed class PricingPlanDefinitionTests\n{\n    [Fact]\n    public void PlanPreservesExplicitMeterReferencesAndProvenance()\n    {\n        PricingPlanDefinition plan = new(\n            "standard-plan",\n            ["text-input", "audio-output"],\n            "catalog:fixture-plan");\n\n        Assert.Equal("standard-plan", plan.StableId);\n        Assert.Equal(["text-input", "audio-output"], plan.MeterStableIds);\n        Assert.Equal("catalog:fixture-plan", plan.ProvenanceId);\n    }\n\n    [Fact]\n    public void PlanRejectsMissingOrDuplicateMeterReferences()\n    {\n        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(\n            "standard-plan",\n            [],\n            "catalog:fixture-plan"));\n        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(\n            "standard-plan",\n            ["text-input", "text-input"],\n            "catalog:fixture-plan"));\n    }\n\n    [Fact]\n    public void PlanRejectsAmbiguousIdentifiersAndInvisibleProvenance()\n    {\n        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(\n            "Standard Plan",\n            ["text-input"],\n            "catalog:fixture-plan"));\n        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(\n            "standard-plan",\n            ["Text Input"],\n            "catalog:fixture-plan"));\n        Assert.Throws<ArgumentException>(() => new PricingPlanDefinition(\n            "standard-plan",\n            ["text-input"],\n            "catalog:\\u200bfixture-plan"));\n    }\n}\n''')

state_path = root / "SESSION_STATE.json"
state = json.loads(state_path.read_text(encoding="utf-8-sig"))
state.update({
    "repository_version": "0.5.0-stage4-foundation-batch11",
    "generated_at_utc": "2026-08-18T13:30:00Z",
    "status": "Stage 3 is final-Windows-certified and promoted. Stage 4 foundation Batches 1-10 are Windows-admitted. Batch 11 adds the missing provider-neutral pricing-plan contract with explicit meter references and provenance while keeping unavailable schema-1.1.5 limit-taxonomy, runtime-policy 1.3, production pricing, and production trust-anchor bytes fail-closed.",
    "next_exact_action": "Windows-admit Stage 4 Batch 11 pricing-plan contract under exact SDK 10.0.400; then recover and authenticate the exact v2.22 pricing schema/seed, runtime-policy 1.3 schema/seed, schema-1.1.5 limit taxonomy and intended production trusted public key before Stage 4 promotion. Do not start Stage 5 or fabricate unavailable controlling-package bytes.",
    "latest_reaudit_completed_at_utc": "2026-08-18T13:30:00Z",
    "stage4_foundation_batch10_admitted": True,
    "stage4_foundation_batch10_admission_run": 32113025375,
    "stage4_foundation_batch10_commit": "e21f1a055e22f99bd6a3d88d6e2802b6d0b6d4da",
    "stage4_foundation_batch10_tests": "257/257 compiled .NET tests passed; 80/80 verifier self-tests; 153/153 deterministic regressions; Release build 0 warnings/0 errors; locked restore and dotnet format passed",
    "stage4_foundation_batch10_source_sha256": "5f4d5bd9550c7beaecff69bc9e50b855384a445db1bd5a9b12623c4594b2e4c9",
    "stage4_foundation_batch10_evidence_artifact": 9315742662,
    "stage4_foundation_batch10_evidence_sha256": "75bf495e893e8d11ad44b5d0b97fcf948e7939fa83702f6a07066e13b8951533",
    "stage4_foundation_batch11": True,
    "stage4_foundation_batch11_admitted": False,
    "stage4_pricing_plan_contract_explicit": True,
})
state_path.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")

write("docs/STAGE4-FOUNDATION-BATCH11.txt", '''CloudScribe Pro — Stage 4 Foundation Batch 11\n\nPurpose\n- Close the remaining provider-neutral pricing-contract gap identified by a fresh v2.22 Stage 4 conformance audit: plans are required alongside meters, allowances, scoped limits, tiers, modifiers, regions, currencies, uncertainty and provenance.\n- Bind the authoritative Batch 10 Windows admission evidence into the current source-changing checkpoint.\n\nPricing-plan contract\n- PricingPlanDefinition has a CloudScribe-stable plan ID, one or more explicit stable meter references, and source provenance.\n- Plan and meter identifiers use the same strict stable-token rules as the existing normalized pricing meter engine.\n- Meter references must be non-empty and unique; no plan silently selects, invents or aliases a meter.\n- Provenance is explicit, bounded and rejects invisible/control text.\n- No provider-specific price, schema field, tax rule, limit taxonomy or production trust anchor is invented by this foundation contract.\n\nAuthoritative Batch 10 evidence\n- Admission run: 32113025375.\n- Admitted commit: e21f1a055e22f99bd6a3d88d6e2802b6d0b6d4da.\n- Windows verifier self-tests: 80/80 passed.\n- Deterministic regressions: 153/153 passed.\n- Compiled tests: 257/257 passed, 0 failed, 0 skipped.\n- Evidence artifact: 9315742662.\n- Evidence artifact SHA-256: 75bf495e893e8d11ad44b5d0b97fcf948e7939fa83702f6a07066e13b8951533.\n- Deterministic admitted source ZIP SHA-256: 5f4d5bd9550c7beaecff69bc9e50b855384a445db1bd5a9b12623c4594b2e4c9.\n\nStill deliberately NOT claimed\n- Exact v2.22 pricing schema/seed import or validator agreement.\n- Exact schema-1.1.5 limit-taxonomy admission.\n- runtime-policy 1.3 schema/seed validation.\n- Any inferred production Ed25519 trust anchor.\n- Stage 4 completion or promotion.\n- Stage 5 start.\n\nNext gate\n- Windows-admit this exact Batch 11 source under .NET SDK 10.0.400 with all verifier, deterministic regression, locked restore, formatter, strict Release analyzer, compiled-test, source-stability, guarded-publication and deterministic-archive gates unchanged.\n- Then recover/authenticate the exact controlling production material before Stage 4 promotion.\n''')

verifier_path = root / "tools/verify_stage4_source.py"
verifier = verifier_path.read_text(encoding="utf-8-sig")
anchor = 'BATCH9_EVIDENCE_SHA256 = "358de956d5deca6b0382794b53721a64eea76a0835e4aa5592a111953945c991"\n'
addition = '''BATCH10_COMMIT = "e21f1a055e22f99bd6a3d88d6e2802b6d0b6d4da"\nBATCH10_RUN = "32113025375"\nBATCH10_SOURCE_SHA256 = "5f4d5bd9550c7beaecff69bc9e50b855384a445db1bd5a9b12623c4594b2e4c9"\nBATCH10_EVIDENCE_ARTIFACT = 9315742662\nBATCH10_EVIDENCE_SHA256 = "75bf495e893e8d11ad44b5d0b97fcf948e7939fa83702f6a07066e13b8951533"\n'''
if addition not in verifier:
    if anchor not in verifier:
        raise SystemExit("Batch 9 verifier anchor missing")
    verifier = verifier.replace(anchor, anchor + addition, 1)
old = '''    if state.get("stage4_foundation_batch10") is not True or state.get("stage4_foundation_batch10_admitted") is not False:\n        return fail("Current Stage 4 Batch 10 must remain a source-changing candidate until Windows admission")\n'''
new = '''    if state.get("stage4_foundation_batch10") is not True or state.get("stage4_foundation_batch10_admitted") is not True:\n        return fail("Stage 4 must preserve the authoritative successful Batch 10 admission")\n    if state.get("stage4_foundation_batch10_commit") != BATCH10_COMMIT or str(state.get("stage4_foundation_batch10_admission_run")) != BATCH10_RUN:\n        return fail("Stage 4 is not bound to the authoritative Batch 10 Windows admission evidence")\n    if state.get("stage4_foundation_batch10_source_sha256") != BATCH10_SOURCE_SHA256:\n        return fail("Stage 4 is not bound to the deterministic Batch 10 admitted source archive")\n    if state.get("stage4_foundation_batch10_evidence_artifact") != BATCH10_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch10_evidence_sha256") != BATCH10_EVIDENCE_SHA256:\n        return fail("Stage 4 is not bound to the authoritative Batch 10 evidence artifact")\n    if state.get("stage4_foundation_batch11") is not True or state.get("stage4_foundation_batch11_admitted") is not False:\n        return fail("Current Stage 4 Batch 11 must remain a source-changing candidate until Windows admission")\n    if state.get("stage4_pricing_plan_contract_explicit") is not True:\n        return fail("Stage 4 must expose an explicit provider-neutral pricing-plan contract")\n'''
if new not in verifier:
    if old not in verifier:
        raise SystemExit("Batch 10 verifier state block missing")
    verifier = verifier.replace(old, new, 1)
meter = '''        require_text(root, "src/CloudScribe.Domain/Pricing/PricingMeterDefinition.cs",\n            "The final pricing tier must be open-ended", "one currency and exact integer scale")\n'''
plan = '''        require_text(root, "src/CloudScribe.Domain/Pricing/PricingPlanDefinition.cs",\n            "PricingPlanDefinition", "MeterStableIds", "ProvenanceId",\n            "requires at least one meter reference", "meter references must be unique")\n        require_text(root, "tests/CloudScribe.Domain.Tests/PricingPlanDefinitionTests.cs",\n            "PlanPreservesExplicitMeterReferencesAndProvenance",\n            "PlanRejectsMissingOrDuplicateMeterReferences",\n            "PlanRejectsAmbiguousIdentifiersAndInvisibleProvenance")\n        require_text(root, "docs/STAGE4-FOUNDATION-BATCH11.txt",\n            "plans are required alongside meters", "Admission run: 32113025375",\n            "257/257 passed", "Stage 4 completion or promotion", "Stage 5 start")\n'''
if plan not in verifier:
    if meter not in verifier:
        raise SystemExit("Pricing meter verifier anchor missing")
    verifier = verifier.replace(meter, meter + plan, 1)
old_print = 'print("PASS: Stage 4 foundation preserves promoted Stage 3 lineage and admitted Batches 1-9, strict bounded JSON, truthful cost/account/capability contracts, external-only empty-by-default Ed25519 catalog trust, persistent append-only catalog history, separate inert user pricing overrides, provenance-bearing quota observations, durable non-secret provider accounts, append-only capability evidence, lazy fake-provider and deterministic fake-catalog coverage, Windows OS-vault storage, explicit provider endpoint/model/alias/voice/operation/governance/data-handling references, and a provider-neutral exact-integer pricing meter/cost engine with fail-closed modifier and usage-scope validation that never guesses unresolved tax/credit/FX or pretends unavailable pricing, limit-taxonomy, or runtime-policy bytes are admitted.")'
new_print = 'print("PASS: Stage 4 foundation preserves promoted Stage 3 lineage and admitted Batches 1-10, strict bounded JSON, truthful cost/account/capability contracts, external-only empty-by-default Ed25519 catalog trust, persistent append-only catalog history, separate inert user pricing overrides, provenance-bearing quota observations, durable non-secret provider accounts, append-only capability evidence, lazy fake-provider and deterministic fake-catalog coverage, Windows OS-vault storage, explicit provider endpoint/model/alias/voice/operation/governance/data-handling references, and provider-neutral provenance-bearing pricing plans plus the exact-integer pricing meter/cost engine with fail-closed modifier and usage-scope validation that never guesses unresolved tax/credit/FX or pretends unavailable pricing, limit-taxonomy, or runtime-policy bytes are admitted.")'
if old_print in verifier:
    verifier = verifier.replace(old_print, new_print, 1)
verifier_path.write_text(verifier, encoding="utf-8", newline="\n")

tests_path = root / "tests/test_verification_tools.py"
tests = tests_path.read_text(encoding="utf-8-sig")
new_test = '''    def test_rejects_pricing_plan_contract_regression(self):\n        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-pricing-plan-") as temporary:\n            root = _copy_source(Path(temporary))\n            path = root / "SESSION_STATE.json"\n            payload = json.loads(path.read_text(encoding="utf-8"))\n            payload["stage4_pricing_plan_contract_explicit"] = False\n            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")\n            result = _run_tool("verify_stage4_source.py", cwd=root)\n            self.assertNotEqual(result.returncode, 0)\n            self.assertIn("pricing-plan contract", result.stderr)\n\n'''
if new_test not in tests:
    marker = "\nclass Stage2EvidenceInventoryCliTests(unittest.TestCase):\n"
    if marker not in tests:
        raise SystemExit("Stage4 mutation insertion marker missing")
    tests = tests.replace(marker, "\n" + new_test + marker, 1)
tests_path.write_text(tests, encoding="utf-8", newline="\n")

runner_path = root / "tools/run_verifier_self_tests.py"
runner = runner_path.read_text(encoding="utf-8-sig")
runner = runner.replace('(\"Stage4SourceContractTests\", 25)', '(\"Stage4SourceContractTests\", 26)', 1)
runner_path.write_text(runner, encoding="utf-8", newline="\n")

subprocess.run([sys.executable, "tools/update_sha256_manifest.py"], cwd=root, check=True)
