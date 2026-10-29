using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public static class ChirpPolicyValidation
{
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void RequireValid(ChirpArticleAdmissionPolicy p, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.SchemaVersion != 1 || p.ExpiresAtUtc <= now || !IsHash(p.AppSha256) || !IsHash(p.InfrastructureSha256)
            || !IsHash(p.ProfileRootSha256) || !IsHash(p.SourceApprovalSha256) || string.IsNullOrWhiteSpace(p.AccountId)
            || string.IsNullOrWhiteSpace(p.ProjectId) || !string.Equals(p.Currency, "USD", StringComparison.Ordinal) || p.Scale != 2
            || p.Articles is not { Count: >= 1 and <= 200 } || p.MaximumNewAttempts is < 1 or > 10_000
            || p.MaximumAdditionalReservationMinorUnits < 1 || p.Budget is null)
            throw new InvalidDataException("The reviewed article-plan policy is absent, invalid or expired.");
        var b = p.Budget;
        if (b.ProjectCommittedMinorUnits < 0 || b.AllocationCommittedMinorUnits < 0 || !IsHash(b.ReviewedEvidenceSha256)
            || b.ProjectCeilingMinorUnits < b.ProjectCommittedMinorUnits || b.AllocationCeilingMinorUnits < b.AllocationCommittedMinorUnits
            || p.MaximumAdditionalReservationMinorUnits > b.ProjectCeilingMinorUnits - b.ProjectCommittedMinorUnits
            || p.MaximumAdditionalReservationMinorUnits > b.AllocationCeilingMinorUnits - b.AllocationCommittedMinorUnits)
            throw new InvalidDataException("The article-plan budget does not preserve its reviewed retained baseline.");
        foreach (var pins in new[] { b.QueueRows, b.SpendRows, b.RecoveryRows, b.TransitionRows, p.ScopeRows, p.PriorArticleAttemptRows }) RequirePins(pins);
        if (p.Articles.Select(x => x.Proposal.DocumentId).Distinct().Count() != p.Articles.Count
            || p.Articles.Select(x => x.Proposal.PlanSha256).Distinct(StringComparer.Ordinal).Count() != p.Articles.Count)
            throw new InvalidDataException("Article-plan identities are ambiguous.");
        foreach (var article in p.Articles)
        {
            if (article.Proposal is null || !IsHash(article.Proposal.PlanSha256) || article.Lineage is null
                || article.ReusedAudio is not { Count: <= 200 } || article.AllowedSegments is not { Count: >= 1 and <= 10_000 }
                || article.AllowedSegments.Select(x => x.CoverageOrdinal).Distinct().Count() != article.AllowedSegments.Count
                || article.AllowedSegments.Any(x => x.MaximumMinorUnits < 1 || x.MaximumMinorUnits > p.MaximumAdditionalReservationMinorUnits
                    || article.Proposal.Parts.Count(y => y.CoverageOrdinal == x.CoverageOrdinal && y.Range.ReusedReceiptSha256 is null) != 1))
                throw new InvalidDataException("The allowed internal requests are missing, duplicated or refer to accepted coverage.");
        }
    }

    private static void RequirePins(IReadOnlyList<RecoveryFinancialRowPin>? pins)
    {
        if (pins is not { Count: <= 10_000 } || pins.Any(x => !IsHash(x.Identity) || !IsHash(x.Sha256))
            || pins.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count() != pins.Count)
            throw new InvalidDataException("Reviewed financial pins are missing or ambiguous.");
    }
}
