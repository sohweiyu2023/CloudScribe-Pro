using System.Security.Cryptography;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationSubmissionEnvelope(
    string AccountId,
    string CredentialReferenceId,
    string CapabilityProvenanceId,
    string PricingProvenanceId,
    int RequestRevision,
    string VoiceName,
    string AudioEncoding,
    string CompiledPayloadSha256,
    int CompiledPayloadBytes)
{
    public static GoogleGenerationSubmissionEnvelope Create(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        string pricingProvenanceId,
        int requestRevision,
        string voiceName,
        string audioEncoding,
        ReadOnlySpan<byte> compiledPayload,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(capabilities);
        account.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(pricingProvenanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioEncoding);
        if (requestRevision < 0) throw new ArgumentOutOfRangeException(nameof(requestRevision));
        if (compiledPayload.IsEmpty) throw new ArgumentException("Compiled provider payload is required.", nameof(compiledPayload));
        if (!string.Equals(account.AccountId, capabilities.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google account and capability snapshot identities do not match.");
        }

        capabilities.RequireSupported(voiceName, audioEncoding, compiledPayload.Length, nowUtc);
        return new GoogleGenerationSubmissionEnvelope(
            account.AccountId,
            account.CredentialReferenceId,
            capabilities.ProvenanceId,
            pricingProvenanceId,
            requestRevision,
            voiceName,
            audioEncoding,
            Convert.ToHexString(SHA256.HashData(compiledPayload)).ToLowerInvariant(),
            compiledPayload.Length);
    }

    public void EnsureStillAuthorized(
        GoogleGenerationAccount account,
        GoogleCapabilitySnapshot capabilities,
        string pricingProvenanceId,
        int requestRevision,
        ReadOnlySpan<byte> compiledPayload,
        DateTimeOffset nowUtc)
    {
        var current = Create(
            account,
            capabilities,
            pricingProvenanceId,
            requestRevision,
            VoiceName,
            AudioEncoding,
            compiledPayload,
            nowUtc);
        if (current != this)
        {
            throw new InvalidOperationException("Google billable submission authorization changed after approval; regenerate estimate/approval before submitting.");
        }
    }
}
