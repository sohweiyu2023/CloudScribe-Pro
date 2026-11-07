namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioLedgerAdmission
{
    private async Task<(GoogleLongAudioBudgetScope Scope, ChirpArticleAdmissionPolicy Parent, long Ceiling, GoogleLongAudioRecoveryProvenance? Proof)> ReadScopeAsync(
        GoogleLongAudioRequest request, CancellationToken token)
    {
        byte[] scopeBytes = await ChirpPolicyFiles.ReadAsync(scopePath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        byte[] parentBytes = await ChirpPolicyFiles.ReadAsync(parentPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        if (!Same(ChirpPolicyFiles.Hash(scopeBytes), scopeSha) || !Same(ChirpPolicyFiles.Hash(parentBytes), parentSha))
            throw new InvalidDataException("Reviewed Long Audio scope or preserved parent changed.");
        var scope = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetScope>(scopeBytes);
        var parent = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(parentBytes);
        ChirpPolicyValidation.RequireValid(parent, clock.GetUtcNow());
        if (scope.Version != 1 || !Same(scope.ParentPolicySha256, parentSha) || !IsHash(scope.SourceApprovalSha256)
            || !Same(scope.ProjectNumber, request.ProjectNumber) || !Same(scope.CredentialReferenceId, request.CredentialReferenceId)
            || !Same(scope.VoiceName, request.VoiceName) || !Same(scope.LanguageCode, request.LanguageCode) || !Same(scope.AudioEncoding, request.AudioEncoding)
            || !request.OutputGcsUri.StartsWith("gs://" + scope.OutputBucket + "/", StringComparison.Ordinal)
            || scope.TotalGoogleCapUsdCents <= 0 || scope.TaxFxProvisionUsdCents < 0 || scope.UsdPerMillionCharacters < 0
            || string.IsNullOrWhiteSpace(scope.PricingProvenance) || !Same(parent.Currency, "USD") || parent.Scale != 2
            || parent.Budget is null || parent.Budget.ProjectCommittedMinorUnits < 0
            || parent.Budget.ProjectCeilingMinorUnits < parent.Budget.ProjectCommittedMinorUnits
            || scope.ApprovedRequestCeilingsUsdCents is null
            || scope.ApprovedRequestCeilingsUsdCents.Any(x => !IsHash(x.Key) || x.Value <= 0)
            || scope.AdaptiveRecoveryRootBindings is { } roots && (roots.Distinct(StringComparer.Ordinal).Count() != roots.Count
                || roots.Any(x => !IsHash(x) || !scope.ApprovedRequestCeilingsUsdCents.ContainsKey(x))
                || roots.Count > 0 && !Same(scope.AudioEncoding, "LINEAR16")))
            throw new InvalidDataException("The exact Long Audio request is outside its independently reviewed authority.");
        GoogleLongAudioFailedRangeReplacementPlanner.RequireScopeShape(scope);
        GoogleLongAudioDeferredRange.RequireScope(scope);
        var proof = scope.ApprovedRequestCeilingsUsdCents.ContainsKey(request.BindingSha256) ? null : RequireRecoveryProof(request, scope);
        long ceiling = ResolveCeiling(request, scope, proof);
        var review = GoogleLongAudioCostReview.Prepare(request, scope.UsdPerMillionCharacters,
            parent.Budget.ProjectCeilingMinorUnits, scope.TaxFxProvisionUsdCents, scope.TotalGoogleCapUsdCents, scope.PricingProvenance);
        if (!review.WithinProvidedCap)
            throw new GoogleLongAudioBudgetExceededException(review.ProjectedProvisionUsdCents, scope.TotalGoogleCapUsdCents);
        if (review.EstimatedSynthesisUsdCents > ceiling)
            throw new InvalidOperationException("The exact Long Audio estimate is not covered by its reviewed commitment.");
        long allApproved = 0;
        foreach (long amount in scope.ApprovedRequestCeilingsUsdCents.Values) allApproved = checked(allApproved + amount);
        foreach (var replacement in scope.FailedRangeReplacements ?? []) allApproved = checked(allApproved + replacement.MaximumUsdCents);
        foreach (var descriptor in scope.DeferredUnknownRanges ?? [])
            if (descriptor.ExplicitRegeneration is { } regeneration) allApproved = checked(allApproved + regeneration.MaximumUsdCents);
        RequireBudget(scope, parent, allApproved);
        return (scope, parent, ceiling, proof);
    }

    private static long ResolveCeiling(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope, GoogleLongAudioRecoveryProvenance? proof)
    {
        if (scope.ApprovedRequestCeilingsUsdCents.TryGetValue(request.BindingSha256, out long amount)) return amount;
        if (proof is null) throw new InvalidDataException("A non-root ceiling requires its exact verified provenance.");
        var approvedReplacement = scope.FailedRangeReplacements?.SingleOrDefault(x => x.ReplacementAttemptId == request.AttemptId);
        if (approvedReplacement is not null) return approvedReplacement.MaximumUsdCents;
        var regeneration = scope.DeferredUnknownRanges?.Select(x => x.ExplicitRegeneration).SingleOrDefault(x => x?.AttemptId == request.AttemptId);
        if (regeneration is not null) return regeneration.MaximumUsdCents;
        return Math.Max(1, checked((long)decimal.Ceiling(request.ConservativeCharacterUnits * scope.UsdPerMillionCharacters / 10_000m)));
    }

    private GoogleLongAudioRecoveryProvenance RequireRecoveryProof(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope) =>
        recoveryAuthority?.RequireProvenance(request, scope)
            ?? throw new InvalidDataException("The exact request has no reviewed admission or recovery family.");
}
