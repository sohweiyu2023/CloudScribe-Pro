using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Infrastructure.Tests;

public sealed class AdmittedV222PricingCatalogContractValidatorTests
{
    [Fact]
    public void AuthenticatedCurrentSeedPassesAdmittedContractAndRequiresManualApproval()
    {
        StrictJsonObjectReader reader = new();
        V222ControlSet controls = new(reader);
        PricingCatalogAdmissionService service = new(
            reader,
            new AdmittedV222PricingCatalogContractValidator(controls),
            new UnavailablePricingCatalogSignatureVerifier());

        PricingCatalogDryRunResult result = service.DryRun(controls.PricingSeedUtf8);

        Assert.Equal(PricingCatalogTrustState.ValidUnsigned, result.TrustState);
        Assert.True(result.CanApprove);
        Assert.Contains("manual approval", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneByteCatalogMutationFailsClosed()
    {
        StrictJsonObjectReader reader = new();
        V222ControlSet controls = new(reader);
        byte[] mutated = controls.PricingSeedUtf8.ToArray();
        int index = Array.IndexOf(mutated, (byte)'C');
        Assert.True(index >= 0);
        mutated[index] = (byte)'D';
        PricingCatalogAdmissionService service = new(
            reader,
            new AdmittedV222PricingCatalogContractValidator(controls),
            new UnavailablePricingCatalogSignatureVerifier());

        PricingCatalogDryRunResult result = service.DryRun(mutated);

        Assert.Equal(PricingCatalogTrustState.ValidationFailed, result.TrustState);
        Assert.False(result.CanApprove);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "catalog.contract.unrecognized-bytes");
    }

    [Fact]
    public void AdmittedControlSetAuthenticatesRuntimePolicyAndLimitsMaterial()
    {
        V222ControlSet controls = new(new StrictJsonObjectReader());

        Assert.NotEmpty(controls.RuntimePolicySchemaUtf8.ToArray());
        Assert.NotEmpty(controls.RuntimePolicySeedUtf8.ToArray());
        Assert.NotEmpty(controls.LimitsContractUtf8.ToArray());
        Assert.NotEmpty(controls.ValidationReportUtf8.ToArray());
    }
}
