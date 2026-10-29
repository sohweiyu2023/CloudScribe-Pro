using CloudScribe.Application.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Pure completeness check before existing FFmpeg assembly. File/queue/source evidence must also be verified before and after assembly.</summary>
public static class ChirpArticleAudioCoverage
{
    public static IReadOnlyList<ArticleAcceptedAudio> RequireComplete(ChirpArticlePlanProposal plan,
        IReadOnlyList<ChirpSegmentAcceptedAudio> segments, IReadOnlyList<ChirpReusedArticleAudio> reused)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(reused);
        if (plan.DocumentId == Guid.Empty || plan.RevisionId == Guid.Empty || !IsHash(plan.PlanSha256) || !IsHash(plan.SourceSha256)
            || plan.SourceScalars is < 1 or > ChirpNarrationPlanner.MaximumSourceScalars
            || plan.Parts.Count is < 1 or > ChirpNarrationPlanner.MaximumCoverageParts
            || segments.Count > ChirpNarrationPlanner.MaximumCoverageParts || reused.Count > ChirpNarrationPlanner.MaximumCoverageParts
            || segments.Count + reused.Count != plan.Parts.Count
            || !plan.Parts.Select(x => x.CoverageOrdinal).SequenceEqual(Enumerable.Range(1, plan.Parts.Count)))
            throw new InvalidOperationException("Complete internal article coverage is unavailable; no export was started.");
        List<ArticleAcceptedAudio> ordered = [];
        var byOrdinal = segments.ToLookup(x => x.CoverageOrdinal);
        var byReceipt = reused.ToLookup(x => x.ReceiptSha256, StringComparer.Ordinal);
        int cursor = 0;
        foreach (var part in plan.Parts)
        {
            if (part.Range.StartScalar != cursor || part.Range.EndScalarExclusive <= cursor || part.Range.EndScalarExclusive > plan.SourceScalars)
                throw new InvalidOperationException("Internal article coverage is missing, overlapping or truncated.");
            cursor = part.Range.EndScalarExclusive;
            ArticleAcceptedAudio audio;
            if (part.Range.ReusedReceiptSha256 is { } receipt)
            {
                var candidates = byReceipt[receipt].ToArray();
                if (!IsHash(receipt) || candidates.Length != 1 || candidates[0].ParentDocumentId != plan.DocumentId
                    || candidates[0].ParentRevisionId != plan.RevisionId || candidates[0].Range != part.Range
                    || part.PayloadSha256 is not null || part.PayloadBytes != 0)
                    throw new InvalidOperationException("Reused accepted article coverage is missing, stale or ambiguous.");
                audio = candidates[0].Audio;
            }
            else
            {
                var candidates = byOrdinal[part.CoverageOrdinal].ToArray();
                if (!IsHash(part.PayloadSha256) || part.PayloadBytes is < 1 or > 5_000
                    || part.Range.EndScalarExclusive - part.Range.StartScalar > ChirpNarrationPlanner.MaximumRequestScalars
                    || candidates.Length != 1 || !string.Equals(candidates[0].PlanSha256, plan.PlanSha256, StringComparison.Ordinal)
                    || candidates[0].Range != part.Range || candidates[0].Audio.DocumentId != plan.DocumentId
                    || candidates[0].Audio.RevisionId != plan.RevisionId
                    || !string.Equals(candidates[0].Audio.PayloadSha256, part.PayloadSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("An accepted internal request is missing, stale or ambiguous.");
                audio = candidates[0].Audio;
            }
            if (!string.Equals(audio.TextSha256, part.Range.TextSha256, StringComparison.Ordinal)
                || !string.Equals(audio.VoiceName, plan.VoiceName, StringComparison.Ordinal)
                || !string.Equals(audio.LanguageCode, plan.LanguageCode, StringComparison.Ordinal))
                throw new InvalidOperationException("Accepted audio does not match exact article coverage and voice.");
            ordered.Add(audio);
        }
        if (cursor != plan.SourceScalars) throw new InvalidOperationException("Internal article coverage does not reach the original cutoff.");
        RequireUniqueBoundedAudio(ordered);
        return ordered.AsReadOnly();
    }

    private static void RequireUniqueBoundedAudio(List<ArticleAcceptedAudio> audio)
    {
        if (audio.Any(x => x.DocumentId == Guid.Empty || x.RevisionId == Guid.Empty
            || !Guid.TryParseExact(x.LocalAttemptId, "N", out _) || string.IsNullOrWhiteSpace(x.IdempotencyKey)
            || string.IsNullOrWhiteSpace(x.AccountId) || string.IsNullOrWhiteSpace(x.AudioPath)
            || !string.Equals(x.OperationId, GoogleGenerationProvider.SynthesizeOperationStableId, StringComparison.Ordinal)
            || !IsHash(x.TextSha256) || !IsHash(x.PayloadSha256) || !IsHash(x.AudioSha256)
            || x.AudioBytes is < 12 or > 256L * 1024 * 1024)
            || audio.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count() != 1
            || audio.Select(x => x.LocalAttemptId).Distinct(StringComparer.Ordinal).Count() != audio.Count
            || audio.Select(x => x.IdempotencyKey).Distinct(StringComparer.Ordinal).Count() != audio.Count
            || audio.Select(x => x.AudioPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != audio.Count
            || audio.Sum(x => x.AudioBytes) > 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Accepted article audio is duplicated, incompatible or exceeds its local size bound.");
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}
