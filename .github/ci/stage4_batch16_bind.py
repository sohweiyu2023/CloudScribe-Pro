from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

BATCH15_RUN = 32429657620
BATCH15_COMMIT = "05bb5a44eddec026c6bdfaa1e4ed39a338640d12"
BATCH15_SOURCE_SHA256 = "d9676c50329a6281d38bb405174fbab0492f240edcc237cc748fb54faadd7c85"
BATCH15_EVIDENCE_ARTIFACT = 9428832464
BATCH15_EVIDENCE_SHA256 = "563359b4bab348e5454b19ddb69e097e697d05ab57090b85900a912bee7c73f4"


def write_lf(path: Path, text: str) -> None:
    path.write_bytes(text.replace("\r\n", "\n").encode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()

    state_path = root / "SESSION_STATE.json"
    state = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert state["repository_version"] == "0.5.0-stage4-foundation-batch15"
    assert state["stage4_foundation_batch15"] is True
    assert state["stage4_foundation_batch15_admitted"] is False
    for key in (
        "stage4_exact_catalog_bytes_available",
        "stage4_catalog_contract_admitted",
        "stage4_runtime_policy_exact_bytes_available",
        "stage4_runtime_policy_contract_admitted",
        "stage4_limit_taxonomy_exact_bytes_available",
        "stage4_limit_taxonomy_contract_admitted",
        "stage4_complete",
        "stage_gate_passed",
    ):
        assert state[key] is False

    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    state["repository_version"] = "0.5.0-stage4-foundation-batch16"
    state["generated_at_utc"] = generated
    state["status"] = (
        "Stage 3 is promoted. Stage 4 foundation Batches 1-15 are Windows-admitted. "
        "Batch 16 adds a fail-closed exact-control material intake/identity gate while binding "
        "Batch 15 admission evidence; unavailable controlling bytes remain unavailable and unadmitted."
    )
    state["next_exact_action"] = (
        "Windows-admit the substantive Batch 16 exact-control intake slice under exact SDK 10.0.400; "
        "then use it to authenticate supplied v2.22 pricing/runtime controls when exact bytes become "
        "available. Continue independent Stage 4 work without fabricating controls. Do not start Stage 5."
    )
    state["latest_reaudit_completed_at_utc"] = generated
    state["stage4_foundation_batch15_admitted"] = True
    state["stage4_foundation_batch15_admission_run"] = BATCH15_RUN
    state["stage4_foundation_batch15_commit"] = BATCH15_COMMIT
    state["stage4_foundation_batch15_tests"] = (
        "262/262 compiled .NET tests passed; 0 failed; 0 skipped; verifier self-tests including "
        "29 Stage4SourceContractTests and 153/153 deterministic material regressions passed; strict "
        "Release/analyzers, native Windows visual/runtime, post-native restore/format/source stability, "
        "special-character launcher and deterministic archive/no-mutation gates passed"
    )
    state["stage4_foundation_batch15_source_sha256"] = BATCH15_SOURCE_SHA256
    state["stage4_foundation_batch15_evidence_artifact"] = BATCH15_EVIDENCE_ARTIFACT
    state["stage4_foundation_batch15_evidence_sha256"] = BATCH15_EVIDENCE_SHA256
    state["stage4_foundation_batch16"] = True
    state["stage4_foundation_batch16_admitted"] = False
    state["stage4_batch16_evidence_binding_checkpoint"] = True
    state["stage4_exact_control_intake_identity_gate"] = True
    write_lf(state_path, json.dumps(state, indent=2) + "\n")

    app = root / "src/CloudScribe.App/CloudScribe.App.csproj"
    text = app.read_text(encoding="utf-8-sig")
    old = "<InformationalVersion>0.5.0-stage4-foundation-batch15</InformationalVersion>"
    new = "<InformationalVersion>0.5.0-stage4-foundation-batch16</InformationalVersion>"
    assert text.count(old) == 1
    write_lf(app, text.replace(old, new))

    inspector_path = root / "src/CloudScribe.Infrastructure/Pricing/ExactPricingControlMaterialInspector.cs"
    assert not inspector_path.exists()
    write_lf(
        inspector_path,
        '''using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class ExactPricingControlMaterialInspector
{
    private readonly StrictJsonObjectReader _reader;

    public ExactPricingControlMaterialInspector(StrictJsonObjectReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public Inspection Inspect(ReadOnlyMemory<byte> utf8Json, string expectedSha256)
    {
        string expected = NormalizeExpectedSha256(expectedSha256);
        string actual = Convert.ToHexString(SHA256.HashData(utf8Json.Span)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return new Inspection(
                false,
                actual,
                null,
                "Control material identity does not match the authenticated expected SHA-256; parsing/admission is blocked.");
        }

        try
        {
            using var document = _reader.Parse(utf8Json);
            return new Inspection(
                true,
                actual,
                null,
                "Control material identity matches and the bytes are strict UTF-8 JSON with an object root. Contract/schema admission remains a separate gate.");
        }
        catch (PricingCatalogFormatException exception)
        {
            return new Inspection(
                true,
                actual,
                exception.Error,
                $"Control material identity matches, but strict JSON intake failed: {exception.Error}.");
        }
    }

    private static string NormalizeExpectedSha256(string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string value = expectedSha256.Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(expectedSha256));
        }

        return value;
    }

    public sealed record Inspection(
        bool IdentityMatched,
        string ActualSha256,
        PricingCatalogFormatError? FormatError,
        string StatusReason)
    {
        public bool StrictJsonObjectAccepted => IdentityMatched && FormatError is null;
    }
}
''',
    )

    inspector_tests = root / "tests/CloudScribe.Infrastructure.Tests/ExactPricingControlMaterialInspectorTests.cs"
    assert not inspector_tests.exists()
    write_lf(
        inspector_tests,
        '''using System.Security.Cryptography;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ExactPricingControlMaterialInspectorTests
{
    [Fact]
    public void ExactIdentityAndStrictObjectAreAcceptedForIntakeOnly()
    {
        byte[] material = "{\"schemaVersion\":\"1.1.5\"}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, Sha256(material));

        Assert.True(result.IdentityMatched);
        Assert.True(result.StrictJsonObjectAccepted);
        Assert.Null(result.FormatError);
        Assert.Contains("separate gate", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityMismatchFailsClosedBeforeAdmission()
    {
        byte[] material = "{\"schemaVersion\":\"1.1.5\"}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, new string('0', 64));

        Assert.False(result.IdentityMatched);
        Assert.False(result.StrictJsonObjectAccepted);
        Assert.Contains("blocked", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchingIdentityStillRejectsHostileDuplicateMemberJson()
    {
        byte[] material = "{\"schemaVersion\":1,\"schemaVersion\":2}"u8.ToArray();
        ExactPricingControlMaterialInspector inspector = new(new StrictJsonObjectReader());

        ExactPricingControlMaterialInspector.Inspection result = inspector.Inspect(material, Sha256(material));

        Assert.True(result.IdentityMatched);
        Assert.False(result.StrictJsonObjectAccepted);
        Assert.Equal(PricingCatalogFormatError.DuplicateProperty, result.FormatError);
    }

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
''',
    )

    di_path = root / "src/CloudScribe.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs"
    di = di_path.read_text(encoding="utf-8-sig")
    anchor_di = "        services.AddSingleton<StrictJsonObjectReader>();\n"
    assert di.count(anchor_di) == 1
    write_lf(di_path, di.replace(anchor_di, anchor_di + "        services.AddSingleton<ExactPricingControlMaterialInspector>();\n"))

    verifier_path = root / "tools/verify_stage4_source.py"
    verifier = verifier_path.read_text(encoding="utf-8-sig")
    anchor = 'BATCH14_EVIDENCE_SHA256 = "7b8c692fe604970dd9dcb2eaf59dd29a015c6149f18269c5f7af6b2c1a18decd"\n'
    addition = anchor + (
        'BATCH15_COMMIT = "05bb5a44eddec026c6bdfaa1e4ed39a338640d12"\n'
        'BATCH15_RUN = "32429657620"\n'
        'BATCH15_SOURCE_SHA256 = "d9676c50329a6281d38bb405174fbab0492f240edcc237cc748fb54faadd7c85"\n'
        'BATCH15_EVIDENCE_ARTIFACT = 9428832464\n'
        'BATCH15_EVIDENCE_SHA256 = "563359b4bab348e5454b19ddb69e097e697d05ab57090b85900a912bee7c73f4"\n'
    )
    assert verifier.count(anchor) == 1
    verifier = verifier.replace(anchor, addition)
    old_block = '''    if state.get("stage4_foundation_batch15") is not True or state.get("stage4_foundation_batch15_admitted") is not False:
        return fail("Current Stage 4 Batch 15 must remain a source-changing candidate until Windows admission")
    if state.get("stage4_batch15_evidence_binding_checkpoint") is not True:
        return fail("Stage 4 Batch 15 must explicitly bind the Batch 14 admission evidence before further source changes")
'''
    new_block = '''    if state.get("stage4_foundation_batch15") is not True or state.get("stage4_foundation_batch15_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 15 admission")
    if state.get("stage4_foundation_batch15_commit") != BATCH15_COMMIT or str(state.get("stage4_foundation_batch15_admission_run")) != BATCH15_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 15 Windows admission evidence")
    if state.get("stage4_foundation_batch15_source_sha256") != BATCH15_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 15 admitted source archive")
    if state.get("stage4_foundation_batch15_evidence_artifact") != BATCH15_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch15_evidence_sha256") != BATCH15_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 15 evidence artifact")
    if state.get("stage4_foundation_batch16") is not True or state.get("stage4_foundation_batch16_admitted") is not False:
        return fail("Current Stage 4 Batch 16 must remain a source-changing candidate until Windows admission")
    if state.get("stage4_batch16_evidence_binding_checkpoint") is not True:
        return fail("Stage 4 Batch 16 must bind the Batch 15 admission evidence as part of its substantive exact-control intake slice")
    if state.get("stage4_exact_control_intake_identity_gate") is not True:
        return fail("Stage 4 Batch 16 must provide a fail-closed exact-control identity/intake gate")
'''
    assert verifier.count(old_block) == 1
    verifier = verifier.replace(old_block, new_block)
    verifier = verifier.replace(
        "admitted Batches 1-14 and current unadmitted Batch 15, strict bounded JSON",
        "admitted Batches 1-15 and current substantive unadmitted Batch 16, strict bounded JSON",
    )
    write_lf(verifier_path, verifier)

    tests_path = root / "tests/test_verification_tools.py"
    tests = tests_path.read_text(encoding="utf-8-sig")
    marker = "class Stage4SourceContractTests(unittest.TestCase):\n"
    assert tests.count(marker) == 1
    test = '''class Stage4SourceContractTests(unittest.TestCase):
    def test_rejects_wrong_batch15_evidence_artifact_binding(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch15-evidence-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_foundation_batch15_evidence_sha256"] = "0" * 64
            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("authoritative Batch 15 evidence artifact", result.stderr)

    def test_rejects_missing_batch16_exact_control_intake_gate(self):
        with tempfile.TemporaryDirectory(prefix="cloudscribe-stage4-batch16-intake-") as temporary:
            root = _copy_source(Path(temporary))
            path = root / "SESSION_STATE.json"
            payload = json.loads(path.read_text(encoding="utf-8"))
            payload["stage4_exact_control_intake_identity_gate"] = False
            path.write_text(json.dumps(payload, indent=2) + "\\n", encoding="utf-8")
            result = _run_tool("verify_stage4_source.py", cwd=root)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("exact-control identity/intake gate", result.stderr)

'''
    write_lf(tests_path, tests.replace(marker, test, 1))

    runner = root / "tools/run_verifier_self_tests.py"
    runner_text = runner.read_text(encoding="utf-8-sig")
    assert runner_text.count('(\"Stage4SourceContractTests\", 29)') == 1
    write_lf(runner, runner_text.replace('(\"Stage4SourceContractTests\", 29)', '(\"Stage4SourceContractTests\", 31)'))

    doc = root / "docs/STAGE4-FOUNDATION-BATCH16.txt"
    assert not doc.exists()
    write_lf(
        doc,
        f"""CloudScribe Pro — Stage 4 Foundation Batch 16 — Exact-Control Intake

Substantive purpose
- Add a fail-closed intake seam for exact external pricing/runtime control material: authenticate bytes against an expected SHA-256 before strict JSON parsing and before any contract admission.
- Identity success is explicitly not schema/semantic admission. Hash mismatch blocks parsing/admission; matching hostile JSON still fails strict parsing.
- Bind exact successful Batch 15 Windows certification evidence in the same substantive slice rather than creating a bookkeeping-only checkpoint.

Authoritative Batch 15 evidence
- Run {BATCH15_RUN}
- Tested head: {BATCH15_COMMIT}
- 262/262 compiled .NET tests passed; 0 failed; 0 skipped.
- Deterministic source archive SHA-256: {BATCH15_SOURCE_SHA256}
- Evidence artifact: {BATCH15_EVIDENCE_ARTIFACT}
- Independently downloaded evidence ZIP SHA-256: {BATCH15_EVIDENCE_SHA256}

Truth boundary
- Exact v2.22 pricing schema/seed bytes remain unavailable and are not imported or reconstructed.
- Runtime-policy 1.3 and schema-1.1.5 limit-taxonomy exact bytes remain unavailable/unadmitted.
- The new intake gate prepares safe authentication of those controls when supplied; it does not claim they are present or validated.
- No production trust anchor or private signing key is fabricated.
- This slice does not claim Stage 4 completion/promotion. Stage 5 remains blocked.
""",
    )
    subprocess.run(["git", "add", "docs/STAGE4-FOUNDATION-BATCH16.txt"], cwd=root, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
