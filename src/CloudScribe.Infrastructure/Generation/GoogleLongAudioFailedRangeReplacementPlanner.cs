using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Compiles only an independently approved replacement for a definitively failed RPC13 range. No provider effects.</summary>
public static class GoogleLongAudioFailedRangeReplacementPlanner
{
    public static void RequireScopeShape(GoogleLongAudioBudgetScope scope)
    {
        if (scope.FailedRangeReplacements is not { } approved) return;
        HashSet<Guid> oldIds = [], newIds = [];
        foreach (var item in approved)
        {
            if (item is null || !Hash(item.RootBindingSha256) || !Hash(item.FailedBindingSha256) || !Hash(item.FailedCheckpointSha256)
                || !Hash(item.SourceSha256) || !Hash(item.ReplacementBindingSha256) || item.MaximumUsdCents <= 0
                || item.FailedAttemptId == Guid.Empty || item.ReplacementAttemptId == Guid.Empty || item.FailedAttemptId == item.ReplacementAttemptId
                || item.StartScalar < 0 || item.EndScalarExclusive <= item.StartScalar || !oldIds.Add(item.FailedAttemptId) || !newIds.Add(item.ReplacementAttemptId)
                || scope.AdaptiveRecoveryRootBindings?.Contains(item.RootBindingSha256, StringComparer.Ordinal) != true
                || !scope.ApprovedRequestCeilingsUsdCents.ContainsKey(item.RootBindingSha256)
                || scope.ApprovedRequestCeilingsUsdCents.ContainsKey(item.ReplacementBindingSha256)
                || string.Equals(item.FailedBindingSha256, item.ReplacementBindingSha256, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.ReplacementOutputGcsUri)
                || scope.DocumentJobs?.Any(x => x.AttemptId == item.ReplacementAttemptId) == true
                || !item.ReplacementOutputGcsUri.StartsWith("gs://" + scope.OutputBucket + "/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid exact failed-range replacement approval.");
        }
        if (oldIds.Overlaps(newIds)) throw new InvalidDataException("A failed-range approval cannot create a replacement chain.");
        foreach (var group in approved.GroupBy(x => x.RootBindingSha256, StringComparer.Ordinal))
        {
            int end = -1;
            foreach (var item in group.OrderBy(x => x.StartScalar))
            { if (item.StartScalar < end) throw new InvalidDataException("Approved replacement ranges overlap."); end = item.EndScalarExclusive; }
        }
    }

    public static GoogleLongAudioRequest Create(string source, ChirpNarrationRange range, GoogleLongAudioRequest root,
        GoogleLongAudioRequest failed, GoogleLongAudioCheckpoint checkpoint, string actualCheckpointSha256,
        GoogleLongAudioFailedRangeReplacement approval, bool hasAcceptedFailedOutput, decimal usdPerMillionCharacters)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (hasAcceptedFailedOutput || checkpoint.State != GoogleLongAudioState.Failed || checkpoint.HttpStatus != 200 || checkpoint.RpcCode != 13
            || checkpoint.FailureKind != GoogleLongAudioFailureKind.TransientOrTimeout || checkpoint.OperationParseFailed
            || !GoogleLongAudioValidation.IsOperation(checkpoint.OperationName, root.ProjectNumber, root.Location)
            || checkpoint.Observation is not
            {
                ReadbackVerified: true, ReadState: GoogleResponseReadState.Complete,
                Truncated: false, Retention: GoogleExplanationRetentionState.SavedPrivately
            }
            || failed.AttemptId == root.AttemptId || !Same(approval.RootBindingSha256, root.BindingSha256) || approval.FailedAttemptId != failed.AttemptId
            || checkpoint.AttemptId != failed.AttemptId || !Same(approval.FailedBindingSha256, failed.BindingSha256)
            || !Same(checkpoint.BindingSha256, failed.BindingSha256) || !Same(checkpoint.PayloadSha256, failed.PayloadSha256)
            || checkpoint.PayloadBytes != failed.PayloadBytes || !Same(checkpoint.OutputGcsUri, failed.OutputGcsUri)
            || !Hash(actualCheckpointSha256) || !Same(approval.FailedCheckpointSha256, actualCheckpointSha256)
            || range.ReusedReceiptSha256 is not null || range.StartScalar != approval.StartScalar || range.EndScalarExclusive != approval.EndScalarExclusive
            || approval.ReplacementAttemptId == Guid.Empty || approval.ReplacementAttemptId == root.AttemptId || approval.ReplacementAttemptId == failed.AttemptId
            || usdPerMillionCharacters < 0 || !Same(root.AudioEncoding, "LINEAR16") || !Same(failed.AudioEncoding, "LINEAR16"))
            throw new InvalidDataException("A matching retained definitive RPC13 failure and exact replacement approval are required.");
        string text = ChirpNarrationPlanner.Slice(source, range);
        using var wire = JsonDocument.Parse(failed.CopyPayload());
        if (!Same(wire.RootElement.GetProperty("input").GetProperty("text").GetString(), text)
            || !Same(approval.SourceSha256, ArticleAudioManifest.HashText(text)) || !Same(approval.SourceSha256, range.TextSha256)
            || !Same(failed.ProjectNumber, root.ProjectNumber) || !Same(failed.Location, root.Location)
            || !Same(failed.CredentialReferenceId, root.CredentialReferenceId) || !Same(failed.VoiceName, root.VoiceName)
            || !Same(failed.LanguageCode, root.LanguageCode)) throw new InvalidDataException("The failed replacement source or settings changed.");
        string folder = root.OutputGcsUri[..(root.OutputGcsUri.LastIndexOf('/') + 1)];
        if (!Same(approval.ReplacementOutputGcsUri, folder + approval.ReplacementAttemptId.ToString("N") + ".wav"))
            throw new InvalidDataException("The approved replacement output identity changed.");
        var replacement = GoogleLongAudioRequest.Create(approval.ReplacementAttemptId, root.ProjectNumber, root.Location, root.CredentialReferenceId,
            approval.ReplacementOutputGcsUri, text, root.LanguageCode, root.VoiceName, "LINEAR16");
        long cents = Math.Max(1, checked((long)decimal.Ceiling(text.Length * usdPerMillionCharacters / 10_000m)));
        if (!Same(replacement.BindingSha256, approval.ReplacementBindingSha256) || cents > approval.MaximumUsdCents)
            throw new InvalidDataException("The exact replacement request or price is outside approval.");
        return replacement;
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
