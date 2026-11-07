namespace CloudScribe.Infrastructure.Generation;

/// <summary>Explicit reviewed permission to work around one immutable unknown interval, never to retry it.</summary>
public sealed record GoogleLongAudioDeferredRange(Guid AttemptId, string RootBindingSha256,
    int StartScalar, int EndScalarExclusive, string CheckpointSha256, string HeldClaimSha256, string LegacyGuardSha256,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GoogleLongAudioExplicitRegeneration? ExplicitRegeneration = null)
{
    internal static void RequireScope(GoogleLongAudioBudgetScope scope)
    {
        if (scope.DeferredUnknownRanges is null) return;
        // This typed migration binds the one owner of the legacy singleton guard. It is not a run,
        // retry or split-count limit; an additional unknown always requires its own reconciliation.
        if (scope.DeferredUnknownRanges.Count != 1 || scope.DeferredUnknownRanges[0] is not { } item
            || item.AttemptId == Guid.Empty || item.StartScalar < 0 || item.EndScalarExclusive <= item.StartScalar
            || !ChirpPolicyValidation.IsHash(item.RootBindingSha256) || !ChirpPolicyValidation.IsHash(item.CheckpointSha256)
            || !ChirpPolicyValidation.IsHash(item.HeldClaimSha256) || !ChirpPolicyValidation.IsHash(item.LegacyGuardSha256)
            || scope.AdaptiveRecoveryRootBindings?.Contains(item.RootBindingSha256, StringComparer.Ordinal) != true)
            throw new InvalidDataException("Deferred continuation must bind the exact reviewed legacy guard owner and source interval.");
        if (item.ExplicitRegeneration is { } replacement
            && (replacement.AttemptId == Guid.Empty || replacement.AttemptId == item.AttemptId
                || !ChirpPolicyValidation.IsHash(replacement.BindingSha256) || replacement.MaximumUsdCents <= 0
                || string.IsNullOrWhiteSpace(replacement.OutputGcsUri)
                || !replacement.OutputGcsUri.StartsWith("gs://" + scope.OutputBucket + "/", StringComparison.Ordinal)
                || scope.DocumentJobs?.Any(x => x.AttemptId == replacement.AttemptId) == true
                || scope.FailedRangeReplacements?.Any(x => x.ReplacementAttemptId == replacement.AttemptId || x.FailedAttemptId == replacement.AttemptId) == true
                || scope.ApprovedRequestCeilingsUsdCents.ContainsKey(replacement.BindingSha256)))
            throw new InvalidDataException("Invalid exact explicit regeneration approval.");
    }
}
