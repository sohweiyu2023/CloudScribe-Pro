namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationCurrentSpendAuthorizationResolver
{
    private readonly IGoogleGenerationSpendAuthorizationStore _store;

    public GoogleGenerationCurrentSpendAuthorizationResolver(IGoogleGenerationSpendAuthorizationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GoogleGenerationSpendAuthorization> ResolveAsync(
        GoogleGenerationSubmissionEnvelope envelope,
        string currency,
        int scale,
        long currentEstimateMinorUnits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(currentEstimateMinorUnits);

        GoogleGenerationSpendAuthorization? authorization = await _store
            .LoadApprovedAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is null)
        {
            throw new InvalidOperationException(
                "No durable Google spend authorization exists for the exact current submission envelope.");
        }

        authorization.EnsureStillAuthorized(
            envelope,
            currency,
            scale,
            currentEstimateMinorUnits);
        return authorization;
    }
}
