using System.Text.Json;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Infrastructure.Tests;

public sealed class PricingCatalogAdmissionServiceTests
{
    private static readonly byte[] DeterministicFakeCatalog = """{"fixture":"stage4-fake-catalog","schemaVersion":"1.1.5"}"""u8.ToArray();

    [Fact]
    public void DeterministicFakePricingCatalogExercisesStrictAdmissionContract()
    {
        PricingCatalogAdmissionService service = new(
            new StrictJsonObjectReader(),
            new AcceptingValidator(),
            new UnavailablePricingCatalogSignatureVerifier());

        PricingCatalogDryRunResult first = service.DryRun(DeterministicFakeCatalog);
        PricingCatalogDryRunResult second = service.DryRun(DeterministicFakeCatalog);

        Assert.Equal(PricingCatalogTrustState.ValidUnsigned, first.TrustState);
        Assert.Equal(first.TrustState, second.TrustState);
        Assert.Equal(first.StatusReason, second.StatusReason);
        Assert.True(first.CanApprove);
    }

    [Fact]
    public void ExactContractUnavailableBlocksApprovalAfterStrictParsing()
    {
        PricingCatalogAdmissionService service = new(
            new StrictJsonObjectReader(),
            new UnavailablePricingCatalogContractValidator(),
            new UnavailablePricingCatalogSignatureVerifier());

        PricingCatalogDryRunResult result = service.DryRun("{\"schemaVersion\":\"1.1.5\"}"u8.ToArray());

        Assert.Equal(PricingCatalogTrustState.ContractUnavailable, result.TrustState);
        Assert.False(result.CanApprove);
        Assert.Contains(result.Diagnostics, item => string.Equals(item.Code, "catalog.contract.unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidUnsignedCatalogRequiresExplicitManualApprovalState()
    {
        PricingCatalogAdmissionService service = new(
            new StrictJsonObjectReader(),
            new AcceptingValidator(),
            new UnavailablePricingCatalogSignatureVerifier());

        PricingCatalogDryRunResult result = service.DryRun("{\"fixture\":true}"u8.ToArray());

        Assert.Equal(PricingCatalogTrustState.ValidUnsigned, result.TrustState);
        Assert.True(result.CanApprove);
        Assert.Contains("manual approval", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignatureMetadataCannotBecomeTrustWithoutExternalVerification()
    {
        PricingCatalogAdmissionService service = new(
            new StrictJsonObjectReader(),
            new AcceptingValidator(),
            new UnavailablePricingCatalogSignatureVerifier());
        PricingCatalogSignature signature = new("external-test-key", new byte[64]);

        PricingCatalogDryRunResult result = service.DryRun("{\"fixture\":true}"u8.ToArray(), signature);

        Assert.Equal(PricingCatalogTrustState.SignatureInvalid, result.TrustState);
        Assert.False(result.CanApprove);
        Assert.Contains(result.Diagnostics, item => string.Equals(item.Code, "catalog.signature.untrusted", StringComparison.Ordinal));
    }

    private sealed class AcceptingValidator : IPricingCatalogContractValidator
    {
        public PricingCatalogContractValidation Validate(JsonElement catalogRoot)
        {
            Assert.Equal(JsonValueKind.Object, catalogRoot.ValueKind);
            return PricingCatalogContractValidation.Valid();
        }
    }
}
