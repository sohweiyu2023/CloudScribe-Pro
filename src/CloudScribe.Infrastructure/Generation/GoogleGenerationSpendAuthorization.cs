namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationSpendAuthorization(
    GoogleGenerationSubmissionEnvelope Envelope,
    string Currency,
    int Scale,
    long AuthorizedMaximumMinorUnits,
    long ApprovedEstimateMinorUnits)
{
    public static GoogleGenerationSpendAuthorization Create(
        GoogleGenerationSubmissionEnvelope envelope,
        string currency,
        int scale,
        long approvedEstimateMinorUnits,
        long authorizedMaximumMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(scale));
        if (approvedEstimateMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(approvedEstimateMinorUnits));
        if (authorizedMaximumMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(authorizedMaximumMinorUnits));
        if (approvedEstimateMinorUnits > authorizedMaximumMinorUnits)
        {
            throw new InvalidOperationException("Approved Google estimate exceeds the authorized spend ceiling.");
        }

        return new GoogleGenerationSpendAuthorization(
            envelope,
            currency.Trim().ToUpperInvariant(),
            scale,
            authorizedMaximumMinorUnits,
            approvedEstimateMinorUnits);
    }

    public void EnsureStillAuthorized(
        GoogleGenerationSubmissionEnvelope envelope,
        string currency,
        int scale,
        long currentEstimateMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (scale is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(scale));
        if (currentEstimateMinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(currentEstimateMinorUnits));

        if (envelope != Envelope)
        {
            throw new InvalidOperationException("Google submission identity changed after spend approval.");
        }

        if (!string.Equals(Currency, currency.Trim(), StringComparison.OrdinalIgnoreCase) || Scale != scale)
        {
            throw new InvalidOperationException("Google provider-billed currency or scale changed after spend approval.");
        }

        if (currentEstimateMinorUnits != ApprovedEstimateMinorUnits)
        {
            throw new InvalidOperationException("Google estimate changed after approval; regenerate approval before submitting.");
        }

        if (currentEstimateMinorUnits > AuthorizedMaximumMinorUnits)
        {
            throw new InvalidOperationException("Google submission exceeds the authorized spend ceiling.");
        }
    }
}
