using CloudScribe.Domain.Observability;

namespace CloudScribe.Domain.Pricing;

public sealed record CostAssessment
{
    private CostAssessment(
        CostEvidenceKind evidenceKind,
        CostUsageScope usageScope,
        ExactMoney? minimum,
        ExactMoney? maximum,
        string statusReason,
        string? provenanceId,
        bool isStale,
        bool isConflicting)
    {
        EvidenceKind = evidenceKind;
        UsageScope = usageScope;
        Minimum = minimum;
        Maximum = maximum;
        StatusReason = NormalizeRequiredText(statusReason, nameof(statusReason), 256);
        ProvenanceId = NormalizeOptionalText(provenanceId, nameof(provenanceId), 160);
        IsStale = isStale;
        IsConflicting = isConflicting;

        if (evidenceKind == CostEvidenceKind.Unknown)
        {
            if (minimum is not null || maximum is not null || ProvenanceId is not null)
            {
                string parameterName = minimum is not null
                    ? nameof(minimum)
                    : maximum is not null
                        ? nameof(maximum)
                        : nameof(provenanceId);
                throw new ArgumentException(
                    "Unknown cost cannot carry a monetary amount or pretend to have pricing provenance.",
                    parameterName);
            }

            return;
        }

        if (minimum is null || maximum is null || ProvenanceId is null)
        {
            string parameterName = minimum is null
                ? nameof(minimum)
                : maximum is null
                    ? nameof(maximum)
                    : nameof(provenanceId);
            throw new ArgumentException("Known cost evidence requires a bounded amount and provenance.", parameterName);
        }

        ExactMoney low = minimum.Value;
        ExactMoney high = maximum.Value;
        low.EnsureValid(nameof(minimum));
        high.EnsureValid(nameof(maximum));
        if (!string.Equals(low.CurrencyCode, high.CurrencyCode, StringComparison.Ordinal) || low.Scale != high.Scale)
        {
            throw new ArgumentException(
                "Cost ranges must use one currency and one exact integer scale.",
                nameof(maximum));
        }
        if (low.Units < 0 || high.Units < low.Units)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Cost ranges must be non-negative and ordered.");
        }
        if (evidenceKind is CostEvidenceKind.Quoted or CostEvidenceKind.ProviderReported or CostEvidenceKind.ReconciledInvoice
            && low != high)
        {
            throw new ArgumentException(
                "Quoted, provider-reported and reconciled invoice costs must be exact rather than ranges.",
                nameof(maximum));
        }
    }

    public CostEvidenceKind EvidenceKind { get; }
    public CostUsageScope UsageScope { get; }
    public ExactMoney? Minimum { get; }
    public ExactMoney? Maximum { get; }
    public string StatusReason { get; }
    public string? ProvenanceId { get; }
    public bool IsStale { get; }
    public bool IsConflicting { get; }
    public bool HasKnownAmount => Minimum is not null && Maximum is not null;
    public bool IsApprovalSafeEstimate =>
        EvidenceKind is CostEvidenceKind.Estimated or CostEvidenceKind.Quoted
        && HasKnownAmount
        && !IsStale
        && !IsConflicting;

    public static CostAssessment Unknown(
        CostUsageScope usageScope,
        string reason,
        bool isStale = false,
        bool isConflicting = false) =>
        new(CostEvidenceKind.Unknown, usageScope, null, null, reason, null, isStale, isConflicting);

    public static CostAssessment Estimate(
        ExactMoney minimum,
        ExactMoney maximum,
        CostUsageScope usageScope,
        string provenanceId,
        string reason = "Estimated from the admitted pricing catalog.",
        bool isStale = false,
        bool isConflicting = false) =>
        new(CostEvidenceKind.Estimated, usageScope, minimum, maximum, reason, provenanceId, isStale, isConflicting);

    public static CostAssessment Quoted(ExactMoney amount, CostUsageScope usageScope, string provenanceId, string reason = "Provider quote.") =>
        new(CostEvidenceKind.Quoted, usageScope, amount, amount, reason, provenanceId, false, false);

    public static CostAssessment ProviderReported(ExactMoney amount, CostUsageScope usageScope, string provenanceId, string reason = "Provider-reported usage cost.") =>
        new(CostEvidenceKind.ProviderReported, usageScope, amount, amount, reason, provenanceId, false, false);

    public static CostAssessment ReconciledInvoice(ExactMoney amount, CostUsageScope usageScope, string provenanceId, string reason = "Reconciled invoice cost.") =>
        new(CostEvidenceKind.ReconciledInvoice, usageScope, amount, amount, reason, provenanceId, false, false);

    private static string NormalizeRequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length > maximumLength || ContainsUnsafeText(normalized))
        {
            throw new ArgumentException($"Value must be 1-{maximumLength} visible characters.", parameterName);
        }
        return normalized;
    }

    private static string? NormalizeOptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }
        return NormalizeRequiredText(value, parameterName, maximumLength);
    }

    private static bool ContainsUnsafeText(string value) => value.Any(static character =>
        char.IsControl(character)
        || char.IsSurrogate(character)
        || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
}
