namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioLedgerAdmission
{
    private async Task<(GoogleLongAudioBudgetScope Scope, ChirpArticleAdmissionPolicy Parent)> ReadScopeAsync(
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
        long ceiling = ResolveCeiling(request, scope);
        var review = GoogleLongAudioCostReview.Prepare(request, scope.UsdPerMillionCharacters,
            parent.Budget.ProjectCeilingMinorUnits, scope.TaxFxProvisionUsdCents, scope.TotalGoogleCapUsdCents, scope.PricingProvenance);
        if (!review.WithinProvidedCap || review.EstimatedSynthesisUsdCents > ceiling)
            throw new InvalidOperationException("The exact Long Audio estimate is not covered by its reviewed commitment.");
        long allApproved = 0;
        foreach (long amount in scope.ApprovedRequestCeilingsUsdCents.Values) allApproved = checked(allApproved + amount);
        RequireBudget(scope, parent, allApproved);
        return (scope, parent);
    }

    private long ResolveCeiling(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope)
    {
        if (scope.ApprovedRequestCeilingsUsdCents.TryGetValue(request.BindingSha256, out long amount)) return amount;
        _ = RequireRecoveryProof(request, scope);
        return Math.Max(1, checked((long)decimal.Ceiling(request.ConservativeCharacterUnits * scope.UsdPerMillionCharacters / 10_000m)));
    }

    private GoogleLongAudioRecoveryProvenance? RecoveryProof(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope) =>
        scope.ApprovedRequestCeilingsUsdCents.ContainsKey(request.BindingSha256) ? null : RequireRecoveryProof(request, scope);

    private GoogleLongAudioRecoveryProvenance RequireRecoveryProof(GoogleLongAudioRequest request, GoogleLongAudioBudgetScope scope) =>
        recoveryAuthority?.RequireProvenance(request, scope)
            ?? throw new InvalidDataException("The exact request has no reviewed admission or recovery family.");
}
