#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from pathlib import Path

PROMOTED_STAGE3 = "beb186bc57f30f3f308e398085bc3af3c94f4020"
FINAL_STAGE3_RUN = "31900688488"
BATCH5_COMMIT = "fdb274a001043f1a81af0e041efc65fed7b26195"
BATCH5_RUN = "32047903725"

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
    if state.get("stage4_foundation_batch6") is not True or state.get("stage4_foundation_batch6_admitted") is not False:
        return fail("Current Stage 4 Batch 6 must remain a source-changing candidate until Windows admission")
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
        require_text(root, "src/CloudScribe.Application/Security/ICredentialVault.cs",
            "CredentialReference", "StoreAsync", "ReadAsync", "DeleteAsync")
        require_text(root, "src/CloudScribe.Infrastructure/Security/WindowsCredentialVault.cs",
            "CredWriteW", "CredReadW", "CredDeleteW", "PersistLocalMachine", "Array.Clear")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/StrictJsonObjectReaderTests.cs",
            "DuplicateProperty", "TopLevelNotObject", "InvalidUtf8", "NaN", "Infinity")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/Stage4ProviderFoundationTests.cs",
            "FakeProviderRemainsLazy", "ProviderCapabilityState.Unknown", "synthesize-speech")
        require_text(root, "tests/CloudScribe.Infrastructure.Tests/PricingCatalogAdmissionServiceTests.cs",
            "ExactContractUnavailableBlocksApprovalAfterStrictParsing",
            "ValidUnsignedCatalogRequiresExplicitManualApprovalState",
            "SignatureMetadataCannotBecomeTrustWithoutExternalVerification")
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

    forbidden_price_markers = ("pricePerMillion", "price_per_million", "0.000016", "15.00 / 1M")
    source_roots = (root / "src/CloudScribe.App", root / "src/CloudScribe.Application", root / "src/CloudScribe.Domain", root / "src/CloudScribe.Infrastructure")
    for source_root in source_roots:
        for path in source_root.rglob("*.cs"):
            text = path.read_text(encoding="utf-8-sig")
            for marker in forbidden_price_markers:
                if marker in text:
                    return fail(f"hard-coded provider-price marker {marker!r} found in {path.relative_to(root)}")

    print("PASS: Stage 4 foundation preserves promoted Stage 3 lineage and admitted Batches 1-5, strict bounded JSON, truthful cost/account/capability contracts, fail-closed catalog trust, persistent append-only catalog history, separate inert user pricing overrides, provenance-bearing quota observations, durable non-secret provider accounts, append-only capability evidence, lazy fake-provider coverage, Windows OS-vault storage, and a provider-neutral exact-integer pricing meter/cost engine that never guesses unresolved tax/credit/FX or pretends unavailable exact pricing bytes or Ed25519 trust are admitted.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
