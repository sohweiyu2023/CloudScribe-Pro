using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Generation;

public static class GoogleGenerationRequestBindingPolicy
{
    public static void RequireBound(
        GenerationProviderRequest request,
        GenerationCacheTrustContext admittedTrust)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admittedTrust);
        admittedTrust.Validate();

        if (!string.Equals(request.ProviderStableId, admittedTrust.ProviderStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Queued Google request provider does not match the admitted v2.23 trust context.");
        if (!string.Equals(request.AccountId, admittedTrust.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Queued Google request account does not match the admitted v2.23 trust context.");
        if (!string.Equals(request.OperationStableId, admittedTrust.OperationStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Queued Google request operation does not match the admitted v2.23 trust context.");
        if (!string.Equals(request.OutputFormat, admittedTrust.OutputFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Queued Google request output format does not match the admitted v2.23 trust context.");
        if (request.CompiledPayload.IsEmpty)
            throw new InvalidOperationException("Queued Google requests require a non-empty compiled payload.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new InvalidOperationException("Queued Google requests require an explicit idempotency key.");
    }
}
