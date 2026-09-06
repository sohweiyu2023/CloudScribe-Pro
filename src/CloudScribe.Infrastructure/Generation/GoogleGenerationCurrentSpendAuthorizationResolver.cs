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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        GoogleGenerationSpendAuthorization? authorization = await _store
            .LoadApprovedAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is null)
        {
            throw new InvalidOperationException(
                "No durable Google spend authorization exists for the exact current submission envelope.");
        }

        if (authorization.Envelope != envelope)
        {
            throw new InvalidOperationException(
                "Durable Google spend authorization does not match the exact current submission envelope.");
        }

        return authorization;
    }

    public async Task<GoogleGenerationSpendAuthorization> ResolveAsync(
        GoogleGenerationSubmissionEnvelope envelope,
        string currency,
        int scale,
        long currentEstimateMinorUnits,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(currentEstimateMinorUnits);

        GoogleGenerationSpendAuthorization authorization = await ResolveAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
        authorization.EnsureStillAuthorized(
            envelope,
            currency,
            scale,
            currentEstimateMinorUnits);
        return authorization;
    }
}
