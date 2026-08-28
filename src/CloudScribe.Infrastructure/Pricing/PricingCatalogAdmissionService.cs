using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;

namespace CloudScribe.Infrastructure.Pricing;

public sealed class PricingCatalogAdmissionService(
    StrictJsonObjectReader reader,
    IPricingCatalogContractValidator contractValidator,
    IPricingCatalogSignatureVerifier signatureVerifier) : IPricingCatalogAdmissionService
{
    public PricingCatalogDryRunResult DryRun(
        ReadOnlyMemory<byte> utf8Catalog,
        PricingCatalogSignature? signature = null)
    {
        using System.Text.Json.JsonDocument document = reader.Parse(utf8Catalog);
        PricingCatalogContractValidation validation = contractValidator.Validate(utf8Catalog, document.RootElement);
        if (!validation.ContractAvailable)
        {
            return new PricingCatalogDryRunResult(
                PricingCatalogTrustState.ContractUnavailable,
                validation.Diagnostics,
                "Catalog structure parsed safely, but the exact controlling contract is unavailable.");
        }
        if (!validation.IsValid)
        {
            return new PricingCatalogDryRunResult(
                PricingCatalogTrustState.ValidationFailed,
                validation.Diagnostics,
                "Catalog failed schema or semantic validation and cannot be approved.");
        }
        if (signature is null)
        {
            return new PricingCatalogDryRunResult(
                PricingCatalogTrustState.ValidUnsigned,
                [],
                "Catalog passed the admitted contract but has no detached signature; explicit manual approval remains required.");
        }

        PricingCatalogSignatureVerification verification = signatureVerifier.Verify(utf8Catalog, signature);
        return verification.IsVerified
            ? new PricingCatalogDryRunResult(
                PricingCatalogTrustState.SignatureVerified,
                [],
                verification.Reason)
            : new PricingCatalogDryRunResult(
                PricingCatalogTrustState.SignatureInvalid,
                [new PricingCatalogDiagnostic("catalog.signature.untrusted", verification.Reason)],
                "Catalog signature could not be verified against the external trusted-key set.");
    }
}
