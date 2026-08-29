using CloudScribe.Application.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationProductionEvidence(
    ProviderAccountSnapshot Account,
    StoredProviderCapabilitySnapshot Capability)
{
    public GoogleGenerationProductionEvidence Validate(DateTimeOffset nowUtc)
    {
        if (Account is null)
        {
            throw new InvalidOperationException("Current Google provider account evidence is missing.");
        }

        if (Capability is null)
        {
            throw new InvalidOperationException("Current Google capability evidence is missing.");
        }

        ProviderAccountReference account = Account.Reference;
        ProviderCapabilitySnapshot capability = Capability.Snapshot;

        if (!string.Equals(account.ProviderStableId, GoogleGenerationProvider.StableProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Current provider account is not the Google generation provider.");
        if (!Account.IsEnabled)
            throw new InvalidOperationException("Current Google provider account is disabled.");
        if (account.CredentialReference is null)
            throw new InvalidOperationException("Current Google provider account has no credential reference.");
        if (string.IsNullOrWhiteSpace(account.EndpointId))
            throw new InvalidOperationException("Current Google provider account has no admitted endpoint identity.");
        if (string.IsNullOrWhiteSpace(account.RegionId))
            throw new InvalidOperationException("Current Google provider account has no admitted region identity.");
        if (Capability.IsStale(nowUtc))
            throw new InvalidOperationException("Current Google capability evidence is stale.");

        ProviderAccountReference captured = capability.Account;
        if (!string.Equals(captured.ProviderStableId, account.ProviderStableId, StringComparison.Ordinal) ||
            !string.Equals(captured.AccountId, account.AccountId, StringComparison.Ordinal) ||
            !string.Equals(captured.CredentialReference?.TargetName, account.CredentialReference.TargetName, StringComparison.Ordinal) ||
            !string.Equals(captured.EndpointId, account.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(captured.RegionId, account.RegionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Current Google account identity changed after capability evidence was captured; refresh capability evidence before generation.");
        }

        ProviderCapability synthesis = capability.GetCapability(GoogleGenerationProvider.SynthesizeOperationStableId);
        if (!synthesis.IsUsable)
            throw new InvalidOperationException($"Current Google synthesis capability is not usable: {synthesis.DisabledReason ?? synthesis.State.ToString()}");

        return this;
    }
}
