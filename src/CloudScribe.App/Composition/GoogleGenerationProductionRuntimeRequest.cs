using System.Security.Cryptography;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed record GoogleGenerationProductionRuntimeRequest(
    string AccountId,
    GoogleGenerationSubmissionEnvelope SubmissionEnvelope,
    string PricingProvenanceId,
    int RequestRevision,
    string Currency,
    int Scale,
    long CurrentEstimateMinorUnits,
    GoogleGenerationUiExecutionSnapshot Snapshot)
{
    public GoogleGenerationProductionRuntimeRequest Validate()
    {
        RequireCanonical(AccountId, nameof(AccountId));
        if (SubmissionEnvelope is null)
            throw new InvalidOperationException("Google runtime request requires a durable submission envelope.");
        RequireCanonical(PricingProvenanceId, nameof(PricingProvenanceId));
        RequireCanonical(Currency, nameof(Currency));
        if (Snapshot is null)
            throw new InvalidOperationException("Google runtime request requires a current UI execution snapshot.");

        if (RequestRevision < 0)
            throw new InvalidOperationException("Google generation request revision cannot be negative.");
        if (Scale is < 0 or > 9)
            throw new InvalidOperationException("Google generation currency scale must be between zero and nine.");
        if (CurrentEstimateMinorUnits < 0)
            throw new InvalidOperationException("Google generation current estimate cannot be negative.");
        if (!string.Equals(AccountId, SubmissionEnvelope.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google runtime request account differs from the durable submission envelope.");
        if (!string.Equals(PricingProvenanceId, SubmissionEnvelope.PricingProvenanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google runtime request pricing provenance differs from the durable submission envelope.");
        if (RequestRevision != SubmissionEnvelope.RequestRevision)
            throw new InvalidOperationException("Google runtime request revision differs from the durable submission envelope.");
        if (Snapshot.ProviderRequest is null)
            throw new InvalidOperationException("Google runtime request requires a bound provider request.");
        if (!string.Equals(AccountId, Snapshot.ProviderRequest.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Google runtime request account differs from the bound provider request.");

        ReadOnlySpan<byte> compiledPayload = Snapshot.ProviderRequest.CompiledPayload.Span;
        if (compiledPayload.IsEmpty)
            throw new InvalidOperationException("Google runtime request requires a compiled provider payload.");
        string compiledPayloadSha256 = Convert.ToHexString(SHA256.HashData(compiledPayload)).ToLowerInvariant();
        if (compiledPayload.Length != SubmissionEnvelope.CompiledPayloadBytes ||
            !string.Equals(compiledPayloadSha256, SubmissionEnvelope.CompiledPayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google runtime request compiled payload differs from the durable submission envelope.");
        }

        return this;
    }

    private static void RequireCanonical(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Google runtime request '{propertyName}' is required.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
        {
            throw new InvalidOperationException($"Google runtime request '{propertyName}' must be canonical.");
        }
    }
}
