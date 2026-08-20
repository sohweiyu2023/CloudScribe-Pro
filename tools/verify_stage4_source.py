#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from pathlib import Path

PROMOTED_STAGE3 = "beb186bc57f30f3f308e398085bc3af3c94f4020"
FINAL_STAGE3_RUN = "31900688488"
BATCH5_COMMIT = "fdb274a001043f1a81af0e041efc65fed7b26195"
BATCH5_RUN = "32047903725"
BATCH6_COMMIT = "527b0f104662f9ed3292e8c594bcffd782cf07bd"
BATCH6_RUN = "32051045651"
BATCH7_COMMIT = "82344da168aa9aaa9ca50c141e85fbf63ed63bbf"
BATCH7_RUN = "32053498219"
BATCH8_COMMIT = "77fea4e152738eafc00efb78242fa95ec4d56ed3"
BATCH8_RUN = "32094911733"
BATCH8_SOURCE_SHA256 = "57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738"
BATCH8_EVIDENCE_ARTIFACT = 9309652831
BATCH8_EVIDENCE_SHA256 = "9dcb5121e43c05d239227bdb169d2f322d2fe023fe578b3d3970e521028bdf00"
BATCH9_COMMIT = "88c58ed876de71518c4d6c538cdbc0697f4606fb"
BATCH9_RUN = "32095990711"
BATCH9_SOURCE_SHA256 = "5cb164c3bcde199a53924293ab5bdb6d888f5e0427eb1dec4fb80852dbc950a9"
BATCH9_EVIDENCE_ARTIFACT = 9310008713
BATCH9_EVIDENCE_SHA256 = "358de956d5deca6b0382794b53721a64eea76a0835e4aa5592a111953945c991"
BATCH10_COMMIT = "e21f1a055e22f99bd6a3d88d6e2802b6d0b6d4da"
BATCH10_RUN = "32113025375"
BATCH10_SOURCE_SHA256 = "5f4d5bd9550c7beaecff69bc9e50b855384a445db1bd5a9b12623c4594b2e4c9"
BATCH10_EVIDENCE_ARTIFACT = 9315742662
BATCH10_EVIDENCE_SHA256 = "75bf495e893e8d11ad44b5d0b97fcf948e7939fa83702f6a07066e13b8951533"
BATCH11_COMMIT = "801952c69b17ab38d7bacb527f9de1401076bc2a"
BATCH11_MERGE_COMMIT = "34ea9435c72ce3229afc5d52cd6851d3a4d43078"
BATCH11_RUN = "32387286642"
BATCH11_SOURCE_SHA256 = "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174"
BATCH11_EVIDENCE_ARTIFACT = 9413736543
BATCH11_EVIDENCE_SHA256 = "5b91ba0b6ed72f7be433308d3faa19655ceec3bab67a751bee4329e3e3965124"
V222_PACKAGE_SHA256 = "22b0609ca1375488ac04c8a807cfb08ad34a08aa883a8dc2984516e64f68f8b3"
V222_PRICING_SCHEMA_SHA256 = "1dc77a16130efa0fa2428e954bbfc5c7d30088283bbaf5b3dddff5694e01972b"
V222_PRICING_SEED_SHA256 = "3e647812dcae11face91b66c3df642f19134de34b8d706e2c2183c87266e8b61"
BATCH12_COMMIT = "eb7236d72765377804b9c8b7131ff4d26d7d6357"
BATCH12_RUN = "32398889342"
BATCH12_SOURCE_SHA256 = "2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889"
BATCH12_EVIDENCE_ARTIFACT = 9418053746
BATCH12_EVIDENCE_SHA256 = "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607"
BATCH13_COMMIT = "2f3761802f58f824c70d824087cf027f03697d38"
BATCH13_RUN = "32408857066"
BATCH13_SOURCE_SHA256 = "e2b2956dd9e6c4fcb6bcdc9e3a2a6fe64adda119f3e512db756a2267437ffe9d"
BATCH13_EVIDENCE_ARTIFACT = 9421654212
BATCH13_EVIDENCE_SHA256 = "72691cb9d090ec83fa51daad7714c02d816f4fb636a855c63e377eb30fcd3ebb"

def fail(message: str) -> int:
    print(f"FAIL: {message}", file=sys.stderr)
    return 1

def require_text(root: Path, relative: str, *needles: str) -> str:
    path = root / relative
    if not path.is_file():
        raise ValueError(f"required Stage 4 source file missing: {relative}")
    text = path.read_text(encoding="utf-8-sig")
    for needle in needles:
        if needle not in text:
            raise ValueError(f"{relative} is missing required Stage 4 contract token: {needle!r}")
    return text

def main() -> int:
    root = Path.cwd().resolve()
    try:
        state = json.loads((root / "SESSION_STATE.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return fail(f"invalid Stage 4 session state: {exc}")
    if state.get("project") != "CloudScribe Pro" or state.get("current_stage") != 4:
        return fail("SESSION_STATE.json does not identify CloudScribe Pro Stage 4")
    if not str(state.get("repository_version", "")).startswith("0.5.0-stage4"):
        return fail(f"unexpected Stage 4 repository version: {state.get('repository_version')!r}")
    if state.get("required_dotnet_sdk") != "10.0.400":
        return fail("Stage 4 must preserve exact SDK 10.0.400")
    if state.get("stage3_complete") is not True or state.get("stage3_promoted") is not True:
        return fail("Stage 4 requires a complete promoted Stage 3 checkpoint")
    if state.get("stage3_promoted_commit") != PROMOTED_STAGE3 or str(state.get("stage3_final_certification_run")) != FINAL_STAGE3_RUN:
        return fail("Stage 4 is not bound to the authoritative Stage 3 promoted evidence")
    if state.get("stage4_started") is not True or state.get("stage4_complete") is not False or state.get("stage_gate_passed") is not False:
        return fail("Stage 4 progress flags are inconsistent")
    if state.get("stage4_exact_catalog_bytes_available") is not False or state.get("stage4_catalog_contract_admitted") is not False:
        return fail("Stage 4 foundation must not pretend the unavailable exact v2.22 catalog bytes were admitted")
    if state.get("stage4_foundation_batch2_admitted") is not True:
        return fail("Stage 4 must record the authoritative successful Batch 2 admission")
    if state.get("stage4_foundation_batch3") is not True or state.get("stage4_foundation_batch3_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 3 admission")
    if state.get("stage4_foundation_batch4") is not True or state.get("stage4_foundation_batch4_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 4 admission")
    if state.get("stage4_foundation_batch5") is not True or state.get("stage4_foundation_batch5_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 5 admission")
    if state.get("stage4_foundation_batch5_commit") != BATCH5_COMMIT or str(state.get("stage4_foundation_batch5_admission_run")) != BATCH5_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 5 Windows admission evidence")
    if state.get("stage4_foundation_batch6") is not True or state.get("stage4_foundation_batch6_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 6 admission")
    if state.get("stage4_foundation_batch6_commit") != BATCH6_COMMIT or str(state.get("stage4_foundation_batch6_admission_run")) != BATCH6_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 6 Windows admission evidence")
    if state.get("stage4_foundation_batch7") is not True or state.get("stage4_foundation_batch7_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 7 admission")
    if state.get("stage4_foundation_batch7_commit") != BATCH7_COMMIT or str(state.get("stage4_foundation_batch7_admission_run")) != BATCH7_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 7 Windows admission evidence")
    if state.get("stage4_foundation_batch8") is not True or state.get("stage4_foundation_batch8_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 8 admission")
    if state.get("stage4_foundation_batch8_commit") != BATCH8_COMMIT or str(state.get("stage4_foundation_batch8_admission_run")) != BATCH8_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 8 Windows admission evidence")
    if state.get("stage4_foundation_batch8_source_sha256") != BATCH8_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 8 admitted source archive")
    if state.get("stage4_foundation_batch8_evidence_artifact") != BATCH8_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch8_evidence_sha256") != BATCH8_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 8 evidence artifact")
    if state.get("stage4_foundation_batch9") is not True or state.get("stage4_foundation_batch9_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 9 admission")
    if state.get("stage4_foundation_batch9_commit") != BATCH9_COMMIT or str(state.get("stage4_foundation_batch9_admission_run")) != BATCH9_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 9 Windows admission evidence")
    if state.get("stage4_foundation_batch9_source_sha256") != BATCH9_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 9 admitted source archive")
    if state.get("stage4_foundation_batch9_evidence_artifact") != BATCH9_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch9_evidence_sha256") != BATCH9_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 9 evidence artifact")
    if state.get("stage4_foundation_batch10") is not True or state.get("stage4_foundation_batch10_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 10 admission")
    if state.get("stage4_foundation_batch10_commit") != BATCH10_COMMIT or str(state.get("stage4_foundation_batch10_admission_run")) != BATCH10_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 10 Windows admission evidence")
    if state.get("stage4_foundation_batch10_source_sha256") != BATCH10_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 10 admitted source archive")
    if state.get("stage4_foundation_batch10_evidence_artifact") != BATCH10_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch10_evidence_sha256") != BATCH10_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 10 evidence artifact")
    if state.get("stage4_foundation_batch11") is not True or state.get("stage4_foundation_batch11_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 11 admission")
    if state.get("stage4_foundation_batch11_commit") != BATCH11_COMMIT or str(state.get("stage4_foundation_batch11_admission_run")) != BATCH11_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 11 Windows admission evidence")
    if state.get("stage4_foundation_batch11_merge_commit") != BATCH11_MERGE_COMMIT:
        return fail("Stage 4 is not bound to the authoritative Batch 11 merge checkpoint")
    if state.get("stage4_foundation_batch11_source_sha256") != BATCH11_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 11 admitted source archive")
    if state.get("stage4_foundation_batch11_evidence_artifact") != BATCH11_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch11_evidence_sha256") != BATCH11_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 11 evidence artifact")
    if state.get("stage4_foundation_batch12") is not True or state.get("stage4_foundation_batch12_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 12 admission")
    if state.get("stage4_foundation_batch12_commit") != BATCH12_COMMIT or str(state.get("stage4_foundation_batch12_admission_run")) != BATCH12_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 12 Windows admission evidence")
    if state.get("stage4_foundation_batch12_source_sha256") != BATCH12_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 12 admitted source archive")
    if state.get("stage4_foundation_batch12_evidence_artifact") != BATCH12_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch12_evidence_sha256") != BATCH12_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 12 evidence artifact")
    if state.get("stage4_foundation_batch13") is not True or state.get("stage4_foundation_batch13_admitted") is not True:
        return fail("Stage 4 must preserve the authoritative successful Batch 13 admission")
    if state.get("stage4_foundation_batch13_commit") != BATCH13_COMMIT or str(state.get("stage4_foundation_batch13_admission_run")) != BATCH13_RUN:
        return fail("Stage 4 is not bound to the authoritative Batch 13 Windows admission evidence")
    if state.get("stage4_foundation_batch13_source_sha256") != BATCH13_SOURCE_SHA256:
        return fail("Stage 4 is not bound to the deterministic Batch 13 admitted source archive")
    if state.get("stage4_foundation_batch13_evidence_artifact") != BATCH13_EVIDENCE_ARTIFACT or state.get("stage4_foundation_batch13_evidence_sha256") != BATCH13_EVIDENCE_SHA256:
        return fail("Stage 4 is not bound to the authoritative Batch 13 evidence artifact")
    if state.get("stage4_foundation_batch14") is not True or state.get("stage4_foundation_batch14_admitted") is not False:
        return fail("Current Stage 4 Batch 14 must remain a source-changing candidate until Windows admission")
    if state.get("stage4_batch14_evidence_binding_checkpoint") is not True:
        return fail("Stage 4 Batch 14 must explicitly bind the Batch 13 admission evidence before further source changes")
    if state.get("controlling_package_expected_sha256") != V222_PACKAGE_SHA256 or state.get("stage4_pricing_schema_expected_sha256") != V222_PRICING_SCHEMA_SHA256 or state.get("stage4_pricing_seed_expected_sha256") != V222_PRICING_SEED_SHA256:
        return fail("Stage 4 remains unbound from the authenticated v2.22 pricing control identities")
    if state.get("stage4_pricing_plan_contract_explicit") is not True:
        return fail("Stage 4 must expose an explicit provider-neutral pricing-plan contract")
    if state.get("stage4_provider_endpoint_reference_explicit") is not True:
        return fail("Stage 4 must expose endpoint and region as an explicit provider-neutral reference")
    if state.get("stage4_provider_model_alias_voice_operation_contracts") is not True:
        return fail("Stage 4 must expose explicit model, alias, voice and operation provider-neutral contracts")
    if state.get("stage4_provider_governance_data_handling_references") is not True:
        return fail("Stage 4 must expose explicit governance and data-handling evidence references")
    if state.get("stage4_deterministic_fake_pricing_catalog_tested") is not True:
        return fail("Stage 4 must retain deterministic fake pricing-catalog admission coverage")
    if state.get("stage4_runtime_policy_exact_bytes_available") is not False or state.get("stage4_runtime_policy_contract_admitted") is not False:
        return fail("Stage 4 must not pretend unavailable runtime-policy 1.3 bytes were admitted")
    if state.get("stage4_limit_taxonomy_exact_bytes_available") is not False or state.get("stage4_limit_taxonomy_contract_admitted") is not False:
        return fail("Stage 4 must not pretend unavailable schema-1.1.5 limit-taxonomy bytes were admitted")
    if state.get("stage4_ed25519_signature_verification_implemented") is not True:
        return fail("Stage 4 Batch 8 must implement real Ed25519 catalog-signature verification")
    if state.get("stage4_trusted_catalog_keys_external_only") is not True or state.get("stage4_built_in_catalog_trusted_key_count") != 0:
        return fail("Stage 4 catalog trust must be external-only and empty by default")
    if state.get("stage4_private_catalog_signing_keys_present") is not False:
        return fail("CloudScribe product source must never contain catalog private signing keys")
    if state.get("stage4_pricing_contract_overrides_separate") is not True:
        return fail("User pricing contract overrides must remain explicitly separate from upstream catalog truth")
    if state.get("stage4_provider_quota_observation_contract") is not True:
        return fail("Provider quota observations must remain an explicit provenance-bearing contract")
    if state.get("stage4_provider_account_registry_persistent") is not True:
        return fail("Provider account metadata must be durably persisted")
    if state.get("stage4_provider_credentials_persisted_in_database") is not False:
        return fail("Provider credentials must never be persisted in the application database")
    if state.get("stage4_provider_capability_history_persistent") is not True:
        return fail("Provider capability evidence history must be durably persisted")
    if state.get("stage4_provider_default_account_selected") is not False:
        return fail("Stage 4 must not silently select a default provider account")
    if state.get("stage4_normalized_pricing_meter_engine") is not True or state.get("stage4_pricing_exact_integer_arithmetic") is not True:
        return fail("Stage 4 Batch 6 must preserve the provider-neutral exact-integer pricing meter engine")
    if state.get("stage4_pricing_unresolved_tax_credit_fx_guessed") is not False:
        return fail("Stage 4 pricing must not guess unresolved tax, credit, or FX treatment")
    if state.get("stage4_pricing_usage_scope_explicit") is not True or state.get("stage4_pricing_catalog_provenance_required") is not True:
        return fail("Stage 4 pricing must preserve explicit usage scope and catalog provenance")
    if state.get("stage4_pricing_modifier_set_validated") is not True:
        return fail("Stage 4 pricing meters must reject malformed modifier sets")
    if state.get("stage4_pricing_usage_scope_enum_validated") is not True:
        return fail("Stage 4 pricing contracts must reject undefined usage-scope enum values")
    if state.get("whole_application_final_claimed") is not False:
        return fail("Stage 4 source incorrectly claims whole-application final")

    try:
        require_text(root, "src/CloudScribe.Domain/Pricing/CostAssessment.cs",
            "CostEvidenceKind.Unknown", "CostEvidenceKind.ProviderReported", "CostEvidenceKind.ReconciledInvoice",
            "IsStale", "IsConflicting", "ExactMoney")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/StrictJsonObjectReader.cs",
            "Utf8JsonReader", "AllowTrailingCommas = false", "JsonCommentHandling.Disallow",
            "DuplicateProperty", "TopLevelNotObject", "DefaultMaximumDocumentBytes")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/PricingCatalogAdmissionService.cs",
            "PricingCatalogTrustState.ContractUnavailable", "PricingCatalogTrustState.ValidUnsigned",
            "PricingCatalogTrustState.SignatureInvalid", "PricingCatalogTrustState.SignatureVerified")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/UnavailablePricingCatalogSignatureVerifier.cs",
            "Metadata or an embedded key is never accepted as catalog trust")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/Ed25519PricingCatalogSignatureVerifier.cs",
            "SignatureAlgorithm.Ed25519", "KeyBlobFormat.RawPublicKey", "algorithm.SignatureSize",
            "algorithm.PublicKeySize", "StringComparer.Ordinal", "Array.Clear(publicKeyBytes)")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/PricingCatalogTrustOptions.cs",
            "CloudScribe:PricingCatalogTrust", "TrustedEd25519PublicKeys", "StringComparer.Ordinal")
        require_text(root, "src/CloudScribe.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs",
            "AddOptions<PricingCatalogTrustOptions>()", "Ed25519PricingCatalogSignatureVerifier")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/Ed25519PricingCatalogSignatureVerifierTests.cs",
            "Rfc8032VectorVerifiesAgainstExternallyConfiguredTrustedKey", "EmptyTrustedKeySetFailsClosed",
            "TamperedCatalogBytesFailVerification", "MalformedConfiguredPublicKeyFailsClosed")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH8.txt",
            "real Ed25519 verification", "shipped trusted-key mapping empty", "does not contain private signing key material")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH9.txt",
            "Admission run: 32094911733", "255/255 passed", "Evidence artifact: 9309652831",
            "57d2a9196395cd6fbbae9cf7b4af830c4619ac323a7c192510c8f0b3372bd738",
            "Do not begin Stage 5")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH10.txt",
            "Admission run: 32095990711", "255/255 passed", "Evidence artifact: 9310008713",
            "5cb164c3bcde199a53924293ab5bdb6d888f5e0427eb1dec4fb80852dbc950a9",
            "runtime-policy 1.3 schema/seed validation", "Stage 4 completion or promotion")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/EfPricingCatalogHistoryStore.cs",
            "explicit user confirmation", "ExpectedCurrentActivationSequence",
            "Rollback can target only", "PricingCatalogApprovalKind.ManualUnsigned",
            "PricingCatalogApprovalKind.VerifiedSignature")
        require_text(root, "src/CloudScribe.Infrastructure/Persistence/Migrations/Stage4PricingCatalogHistory.cs",
            "pricing_catalog_snapshots", "pricing_catalog_activations", "ReferentialAction.Restrict")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderAccountReference.cs",
            "CredentialReference", "EndpointId", "RegionId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderCapabilitySnapshot.cs",
            "StringComparer.Ordinal", "ProviderCapabilityState.Unknown", "ProvenanceId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderEndpointReference.cs",
            "EndpointId", "RegionId", "NormalizeStableId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderModelReference.cs",
            "StableId", "ExactApiAlias", "ResolvedVersion", "ProviderLifecycleState")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderAliasReference.cs",
            "Alias", "TargetStableId", "ProvenanceId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderVoiceReference.cs",
            "StableId", "ExactProviderVoiceId", "ModelStableId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderOperationReference.cs",
            "StableId", "ProviderLifecycleState")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderGovernanceReference.cs",
            "ProfileId", "ProvenanceId")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderDataHandlingReference.cs",
            "ProfileId", "ProvenanceId")
        require_text(root, "src/CloudScribe.Application/Security/ICredentialVault.cs",
            "CredentialReference", "StoreAsync", "ReadAsync", "DeleteAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Security/WindowsCredentialVault.cs",
            "CredWriteW", "CredReadW", "CredDeleteW", "PersistLocalMachine", "Array.Clear")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/StrictJsonObjectReaderTests.cs",
            "DuplicateProperty", "TopLevelNotObject", "InvalidUtf8", "NaN", "Infinity")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/Stage4ProviderFoundationTests.cs",
            "FakeProviderRemainsLazy", "ProviderCapabilityState.Unknown", "synthesize-speech",
            "ProviderNeutralReferencesKeepEverySelectionAndPolicyEvidenceExplicit", "models/acme:v1", "voices/en-US/A")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/PricingCatalogAdmissionServiceTests.cs",
            "ExactContractUnavailableBlocksApprovalAfterStrictParsing",
            "ValidUnsignedCatalogRequiresExplicitManualApprovalState",
            "SignatureMetadataCannotBecomeTrustWithoutExternalVerification",
            "DeterministicFakePricingCatalogExercisesStrictAdmissionContract")
        require_text(root, "src/CloudScribe.App/Design/StageFeatureAvailability.cs",
            "Stage4", "ShowProviderControls: true")
        require_text(root, "src/CloudScribe.App/MainWindow.axaml",
            "No admitted account", "stay disabled with explicit reasons",
            "CATALOG HISTORY", "history inspection never activates a catalog")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/PricingCatalogHistoryStoreTests.cs",
            "ValidUnsignedSnapshotPersistsWithoutSilentActivationAndDeduplicatesByHash",
            "StaleActivationSequenceFailsClosedInsteadOfOverwritingNewerChoice",
            "RollbackTargetsOnlyPreviouslyActiveSnapshotAndAppendsAuditHistory",
            "MigrationCreatesCatalogHistoryTablesAndForeignKeys")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/WindowsCredentialVaultTests.cs",
            "WindowsCredentialManagerRoundTripsAndDeletesEphemeralSecret", "Array.Clear")
        require_text(root, "src/CloudScribe.Infrastructure/Pricing/EfPricingContractOverrideStore.cs",
            "SaveInactiveAsync", "strictJsonReader.Parse", "PricingContractOverrides")
        require_text(root, "src/CloudScribe.Application/Pricing/IPricingContractOverrideStore.cs",
            "SaveInactiveAsync", "ListInactiveAsync")
        require_text(root, "src/CloudScribe.Providers.Abstractions/ProviderQuotaObservation.cs",
            "ProvenanceId", "ExpiresAtUtc", "IsStale")
        require_text(root, "src/CloudScribe.Providers.Abstractions/IProviderQuotaSource.cs",
            "GetQuotaObservationsAsync")
        require_text(root, "src/CloudScribe.App/ViewModels/ShellViewModel.Pricing.cs",
            "Account quota unknown", "PricingContractOverrideSummary", "stored inactive override")
        require_text(root, "src/CloudScribe.Application/Providers/IProviderAccountStore.cs",
            "CreateAsync", "UpdateAsync", "expectedRevision", "ListAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Providers/EfProviderAccountStore.cs",
            "CredentialTargetName", "Revision", "DbUpdateConcurrencyException")
        require_text(root, "src/CloudScribe.Application/Providers/IProviderCapabilitySnapshotStore.cs",
            "SaveAsync", "GetLatestAsync", "ListRecentAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Providers/EfProviderCapabilitySnapshotStore.cs",
            "Capability evidence cannot be persisted for an unregistered provider account", "ProviderCapabilityEntries")
        require_text(root, "src/CloudScribe.Infrastructure/Persistence/Migrations/Stage4ProviderAccountsAndCapabilities.cs",
            "provider_accounts", "provider_capability_snapshots", "provider_capability_entries", "ReferentialAction.Restrict")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/ProviderAccountStoreTests.cs",
            "RegistryHasNoDefaultSelectionOrSecretBearingApi", "CredentialTargetName")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/ProviderCapabilitySnapshotStoreTests.cs",
            "CapabilityEvidenceRequiresRegisteredAccountAndRemainsAppendOnly", "HistoricalCapabilityEvidencePreservesAccountMetadataAtCaptureTime")
        require_text(root, "src/CloudScribe.App/ViewModels/ShellViewModel.Pricing.cs",
            "no default selection", "inspection never refreshes providers")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingMeterDefinition.cs",
            "The final pricing tier must be open-ended", "one currency and exact integer scale")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingPlanDefinition.cs",
            "PricingPlanDefinition", "MeterStableIds", "ProvenanceId",
            "requires at least one meter reference", "meter references must be unique")
        require_text(root, "tests/CloudScribe.Domain.Tests/PricingPlanDefinitionTests.cs",
            "PlanPreservesExplicitMeterReferencesAndProvenance",
            "PlanRejectsMissingOrDuplicateMeterReferences",
            "PlanRejectsAmbiguousIdentifiersAndInvisibleProvenance")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH11.txt",
            "plans are required alongside meters", "Admission run: 32113025375",
            "257/257 passed", "Stage 4 completion or promotion", "Stage 5 start")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH12.txt",
            "Run 32387286642", "262/262", "50750f49c2fda74e99f5b1f8d382778d43d9c72a3ff5dfcc057c2890250e1174",
            "exact v2.22 pricing schema/seed bytes are not yet imported", "Stage 5 remains blocked")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH13.txt",
            "Run 32398889342", "262/262", "82/82", "153/153",
            "2a87cf181e8fcdbe9e6cc9c075512fe24e3c69d248f0f8da9d95035448b80889",
            "9418053746", "01344f43bab48d947dca5ecb1e51c6f9add3c9e90f4ff0adc91c706a235bb607",
            "exact v2.22 pricing schema/seed bytes remain unavailable", "Stage 5 remains blocked")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingTier.cs",
            "PricingTier", "PricePerBlock", "BlockSize")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingAllowance.cs",
            "PricingAllowance", "IncludedQuantity", "CostUsageScope")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingModifier.cs",
            "PricingModifier", "Numerator", "Denominator", "RegionId")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingEstimateRequest.cs",
            "TaxResolved", "CreditsResolved", "ForeignExchangeResolved", "ProvenanceId", "UsageScope")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingCostEngine.cs",
            "CostAssessment.Unknown", "BigInteger", "CeilingDivide", "Normalized pricing meter estimate",
            "foreign exchange")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingMeterDefinition.cs",
            "ValidateModifiers", "Pricing modifiers cannot contain null entries", "stable identifiers must be unique")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingAllowance.cs", "Enum.IsDefined(scope)")
        require_text(root, "src/CloudScribe.Domain/Pricing/PricingEstimateRequest.cs", "Enum.IsDefined(usageScope)")
        require_text(root, "src/CloudScribe.Domain/Pricing/CostAssessment.cs", "Enum.IsDefined(usageScope)")
        require_text(root, "tests/CloudScribe.Domain.Tests/PricingCostEngineTests.cs",
            "MeterRejectsNullAndDuplicateModifiers", "UndefinedUsageScopesAreRejectedByPricingContracts",
            "CostBeyondExactMoneyLimitFailsClosed")
        require_text(root, "tests/CloudScribe.Domain.Tests/PricingCostEngineTests.cs",
            "DeterministicFakeMeterAppliesAllowanceAndTieredBlocks",
            "UnresolvedTaxCreditOrFxNeverProducesPretendAmount",
            "StaleOrConflictingCatalogIsNeverApprovalSafe",
            "MismatchedMeterUnitFailsClosed")
        require_text(root, "docs/STAGE4-FOUNDATION-BATCH6.txt",
            "Exact scaled-integer tier and allowance evaluation",
            "Tax, credits, and foreign exchange are not guessed",
            "No provider-specific hard-coded price")
    except (OSError, ValueError) as exc:
        return fail(str(exc))

    try:
        settings = json.loads((root / "src/CloudScribe.App/appsettings.json").read_text(encoding="utf-8-sig"))
        trusted_keys = settings["CloudScribe"]["PricingCatalogTrust"]["TrustedEd25519PublicKeys"]
    except (OSError, KeyError, TypeError, json.JSONDecodeError) as exc:
        return fail(f"invalid empty-by-default pricing trust configuration: {exc}")
    if trusted_keys != {}:
        return fail("shipped pricing trust configuration must contain zero built-in trusted Ed25519 public keys")

    package_props = (root / "Directory.Packages.props").read_text(encoding="utf-8-sig")
    infrastructure_project = (root / "src/CloudScribe.Infrastructure/CloudScribe.Infrastructure.csproj").read_text(encoding="utf-8-sig")
    if 'PackageVersion Include="NSec.Cryptography" Version="26.4.0"' not in package_props:
        return fail("Stage 4 Batch 8 must bind the reviewed NSec.Cryptography 26.4.0 dependency")
    if 'PackageReference Include="NSec.Cryptography"' not in infrastructure_project:
        return fail("CloudScribe.Infrastructure must reference NSec.Cryptography for Ed25519 verification")

    forbidden_price_markers = ("pricePerMillion", "price_per_million", "0.000016", "15.00 / 1M")
    source_roots = (root / "src/CloudScribe.App", root / "src/CloudScribe.Application", root / "src/CloudScribe.Domain", root / "src/CloudScribe.Infrastructure")
    for source_root in source_roots:
        for path in source_root.rglob("*.cs"):
            text = path.read_text(encoding="utf-8-sig")
            for marker in forbidden_price_markers:
                if marker in text:
                    return fail(f"hard-coded provider-price marker {marker!r} found in {path.relative_to(root)}")

    print("PASS: Stage 4 foundation preserves promoted Stage 3 lineage and admitted Batches 1-13 and current unadmitted Batch 14, strict bounded JSON, truthful cost/account/capability contracts, external-only empty-by-default Ed25519 catalog trust, persistent append-only catalog history, separate inert user pricing overrides, provenance-bearing quota observations, durable non-secret provider accounts, append-only capability evidence, lazy fake-provider and deterministic fake-catalog coverage, Windows OS-vault storage, explicit provider endpoint/model/alias/voice/operation/governance/data-handling references, and provider-neutral provenance-bearing pricing plans plus the exact-integer pricing meter/cost engine with fail-closed modifier and usage-scope validation that never guesses unresolved tax/credit/FX or pretends unavailable pricing, limit-taxonomy, or runtime-policy bytes are admitted.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
