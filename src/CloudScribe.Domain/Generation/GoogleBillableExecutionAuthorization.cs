using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Domain.Generation;

public sealed record GoogleBillableExecutionAuthorization(
    string AccountId,
    string CapabilityProvenanceSha256,
    string PricingProvenanceSha256,
    string CompiledPayloadSha256,
    string Currency,
    int CurrencyScale,
    long AuthorizedMaximumMinorUnits,
    long ApprovedEstimateMinorUnits,
    long RequestRevision,
    string AuthorizationSha256)
{
    public static GoogleBillableExecutionAuthorization Create(
        string accountId,
        string capabilityProvenanceSha256,
        string pricingProvenanceSha256,
        string compiledPayloadSha256,
        string currency,
        int currencyScale,
        long authorizedMaximumMinorUnits,
        long approvedEstimateMinorUnits,
        long requestRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ValidateHash(capabilityProvenanceSha256, nameof(capabilityProvenanceSha256));
        ValidateHash(pricingProvenanceSha256, nameof(pricingProvenanceSha256));
        ValidateHash(compiledPayloadSha256, nameof(compiledPayloadSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegative(currencyScale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(currencyScale, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(authorizedMaximumMinorUnits);
        ArgumentOutOfRangeException.ThrowIfNegative(approvedEstimateMinorUnits);
        if (approvedEstimateMinorUnits > authorizedMaximumMinorUnits)
            throw new InvalidOperationException("Approved Google estimate exceeds the authorized spend ceiling.");
        ArgumentOutOfRangeException.ThrowIfNegative(requestRevision);

        var canonical = string.Join("\n",
            accountId,
            capabilityProvenanceSha256.ToLowerInvariant(),
            pricingProvenanceSha256.ToLowerInvariant(),
            compiledPayloadSha256.ToLowerInvariant(),
            currency.ToUpperInvariant(),
            currencyScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            authorizedMaximumMinorUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            approvedEstimateMinorUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requestRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return new GoogleBillableExecutionAuthorization(
            accountId,
            capabilityProvenanceSha256.ToLowerInvariant(),
            pricingProvenanceSha256.ToLowerInvariant(),
            compiledPayloadSha256.ToLowerInvariant(),
            currency.ToUpperInvariant(),
            currencyScale,
            authorizedMaximumMinorUnits,
            approvedEstimateMinorUnits,
            requestRevision,
            hash);
    }

    public void EnsureExecutionStillAuthorized(
        string accountId,
        string capabilityProvenanceSha256,
        string pricingProvenanceSha256,
        string compiledPayloadSha256,
        string currency,
        int currencyScale,
        long currentEstimateMinorUnits,
        long requestRevision)
    {
        var current = Create(
            accountId,
            capabilityProvenanceSha256,
            pricingProvenanceSha256,
            compiledPayloadSha256,
            currency,
            currencyScale,
            AuthorizedMaximumMinorUnits,
            currentEstimateMinorUnits,
            requestRevision);

        if (!string.Equals(AccountId, current.AccountId, StringComparison.Ordinal) ||
            !string.Equals(CapabilityProvenanceSha256, current.CapabilityProvenanceSha256, StringComparison.Ordinal) ||
            !string.Equals(PricingProvenanceSha256, current.PricingProvenanceSha256, StringComparison.Ordinal) ||
            !string.Equals(CompiledPayloadSha256, current.CompiledPayloadSha256, StringComparison.Ordinal) ||
            !string.Equals(Currency, current.Currency, StringComparison.Ordinal) ||
            CurrencyScale != current.CurrencyScale ||
            RequestRevision != current.RequestRevision ||
            ApprovedEstimateMinorUnits != current.ApprovedEstimateMinorUnits)
        {
            throw new InvalidOperationException("Google billable execution authorization is stale or does not match the immutable request.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(AuthorizationSha256),
            Convert.FromHexString(current.AuthorizationSha256)))
        {
            throw new InvalidOperationException("Google billable execution authorization integrity check failed.");
        }
    }

    private static void ValidateHash(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a SHA-256 hexadecimal value.", name);
    }
}
