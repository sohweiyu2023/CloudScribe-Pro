using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class InstalledRecoveryBudgetEvidenceResolver
{
    private static readonly JsonSerializerOptions EnvelopeJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static (string Child, long Reserved)? ValidateAdditionalReservation(RecoveryAdmissionPolicy p, object?[] row,
        IReadOnlyList<RecoveryFinancialSnapshot.QueueState> queues)
    {
        if (row.Length != 27 || row[0] is not string id || !Guid.TryParseExact(id, "N", out var authorizationId)
            || row[13] is not string child || !string.Equals(child, GoogleExplicitRecoveryService.KeyPrefix + id, StringComparison.Ordinal)
            || !Equals(row[8], p.AccountId) || !Equals(row[9], p.ProjectId)
            || !Equals(row[10], GoogleGenerationProvider.SynthesizeOperationStableId)
            || !Equals(row[19], p.Currency) || row[20] is not long scale || scale != p.Scale
            || row[21] is not long estimate || row[22] is not long reserved || estimate < 0 || reserved < estimate
            || !Equals(row[23], p.Budget.ReviewedEvidenceSha256) || row[25] is not long consumed || consumed is < 0 or > 1) return null;
        var anchors = p.Predecessors.Where(x => Equals(row[5], x.HistoricalKey) && Equals(row[6], x.HistoricalStateSha256)
            && Equals(row[7], x.AdmissionSha256) && Equals(row[1], x.LogicalNarrationId)
            && Equals(row[2], x.DocumentId.ToString("D")) && Equals(row[3], x.RevisionId.ToString("D"))
            && Equals(row[4], x.TextSha256) && Equals(row[17], x.OriginalPayloadSha256)).ToArray();
        if (anchors.Length != 1 || row[15] is not string json || json.Length > 16384) return null;
        GoogleGenerationSubmissionEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<GoogleGenerationSubmissionEnvelope>(json, EnvelopeJson); }
        catch (JsonException) { return null; }
        if (envelope is null || envelope.RecoveryAuthorizationId != authorizationId
            || !Equals(envelope.AccountId, p.AccountId) || !Equals(envelope.CompiledPayloadSha256, row[17])
            || row[18] is not long bytes || envelope.CompiledPayloadBytes != bytes) return null;
        var matches = queues.Where(x => string.Equals(x.Account, p.AccountId, StringComparison.Ordinal)
            && string.Equals(x.Operation, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            && string.Equals(x.Key, child, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return null;
        var queue = matches[0];
        if (consumed == 0 && (queue.Attempted != 0 || queue.Unresolved != 0 || queue.ProviderId is not null || queue.LocalAttempt is not null)) return null;
        if (consumed == 1 && (queue.Attempted != 1 || row[26] is not string attempt || string.IsNullOrWhiteSpace(attempt)
            || !Equals(queue.LocalAttempt, attempt) || !Equals(queue.PayloadSha256, row[17]) || !Equals(queue.PayloadBytes, row[18]))) return null;
        return (child, reserved);
    }
}
