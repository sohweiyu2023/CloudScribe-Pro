using System.Security.Cryptography;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class InstalledRecoveryAdmissionStore
{
    private bool ValidPolicy(RecoveryAdmissionPolicy p) => p.SchemaVersion is 2 or 3 && p.ExpiresAtUtc > clock.GetUtcNow()
        && IsHash(p.AppSha256) && IsHash(p.InfrastructureSha256) && IsHash(p.ProfileRootSha256) && !string.IsNullOrWhiteSpace(p.AccountId)
        && !string.IsNullOrWhiteSpace(p.ProjectId) && p.Currency is { Length: 3 } && p.Currency.All(char.IsAsciiLetterUpper)
        && p.Scale is >= 0 and <= 9 && p.HistoricalAudio is { Count: <= 200 } && p.Predecessors is { Count: <= 200 }
        && p.Budget is not null && p.Budget.ProjectCommittedMinorUnits >= 0 && p.Budget.AllocationCommittedMinorUnits >= 0
        && p.Budget.ProjectCeilingMinorUnits >= p.Budget.ProjectCommittedMinorUnits
        && p.Budget.AllocationCeilingMinorUnits >= p.Budget.AllocationCommittedMinorUnits && IsHash(p.Budget.ReviewedEvidenceSha256)
        && ValidPins(p.Budget.QueueRows) && ValidPins(p.Budget.SpendRows) && ValidPins(p.Budget.RecoveryRows)
        && p.HistoricalAudio.Select(x => x.EvidenceId).Distinct(StringComparer.Ordinal).Count() == p.HistoricalAudio.Count
        && p.HistoricalAudio.All(x => !string.IsNullOrWhiteSpace(x.EvidenceId) && x.Anchor is not null
            && string.Equals(x.Anchor.AccountId, p.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Anchor.OperationId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal))
        && (p.SchemaVersion == 2 ? p.Budget.TransitionRows is null && p.Predecessors.All(x => x.Successor is null)
            : string.Equals(p.Currency, "USD", StringComparison.Ordinal) && p.Scale == 2
                && p.Predecessors.Count == 1 && p.HistoricalAudio.Count == 0 && ValidPins(p.Budget.TransitionRows)
                && p.Predecessors.All(x => x.ExpiresAtUtc <= p.ExpiresAtUtc && ValidSuccessor(x, p.Budget)))
        && p.Predecessors.Select(x => x.HistoricalKey).Distinct(StringComparer.Ordinal).Count() == p.Predecessors.Count
        && p.Predecessors.Select(x => x.AdmissionSha256).Distinct(StringComparer.Ordinal).Count() == p.Predecessors.Count
        && p.Predecessors.All(x => string.Equals(x.AccountId, p.AccountId, StringComparison.Ordinal)
            && string.Equals(x.ProjectId, p.ProjectId, StringComparison.Ordinal)
            && string.Equals(x.OperationId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && IsHash(x.AdmissionSha256) && x.MaximumNewAttempts is >= 1 and <= 200
            && x.MaximumAdditionalReservationMinorUnits >= 0);

    private static bool ValidSuccessor(GoogleRecoveryPredecessorEvidence e, RecoveryBudgetBaseline budget)
    {
        var s = e.Successor;
        if (s is null || e.MaximumNewAttempts != 1 || e.MaximumAdditionalReservationMinorUnits != 3
            || s.PriorAuthorizationId == Guid.Empty || !string.Equals(s.PriorChildKey, GoogleExplicitRecoveryService.KeyPrefix + s.PriorAuthorizationId.ToString("N"), StringComparison.Ordinal) || !IsHash(s.PriorAdmissionSha256) || !IsHash(s.PriorStateSha256) || !IsHash(s.FinancialStateSha256)
            || !IsHash(s.SourceApprovalSha256) || !string.Equals(s.SourceApprovalSha256, s.SourceApprovalSha256.ToLowerInvariant(), StringComparison.Ordinal) || string.Equals(s.PriorAdmissionSha256, e.AdmissionSha256, StringComparison.Ordinal) || !string.Equals(Hash(JsonSerializer.SerializeToUtf8Bytes(e with { AdmissionSha256 = "" })), e.AdmissionSha256, StringComparison.Ordinal)) return false;
        var t = s.AllocationTransition;
        return t is null || (IsHash(t.PriorScopeSha256) && string.Equals(t.SourceApprovalSha256, s.SourceApprovalSha256, StringComparison.Ordinal) && t.PriorAllocationCeilingMinorUnits >= 0 && t.NewAllocationCeilingMinorUnits > t.PriorAllocationCeilingMinorUnits
            && t.NewAllocationCeilingMinorUnits == budget.AllocationCeilingMinorUnits
            && t.NewRemainingMinorUnits == checked(budget.AllocationCeilingMinorUnits - budget.AllocationCommittedMinorUnits));
    }

    private static bool ValidPins(IReadOnlyList<RecoveryFinancialRowPin>? pins) => pins is { Count: <= 10000 }
        && pins.All(x => !string.IsNullOrWhiteSpace(x.Identity) && IsHash(x.Sha256))
        && pins.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count() == pins.Count;

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        RejectLinkedPolicyPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is < 1 || stream.Length > maximum) throw new InvalidDataException("Recovery admission input exceeds its bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        RejectLinkedPolicyPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is < 1 or > 256L * 1024 * 1024) throw new InvalidDataException("Recovery build input exceeds its bound.");
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void RejectLinkedPolicyPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Linked recovery admission inputs are refused.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() != 1))
                throw new InvalidDataException("Recovery admission properties are ambiguous.");
            foreach (var property in element.EnumerateObject()) RequireUniqueProperties(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
    }
}
