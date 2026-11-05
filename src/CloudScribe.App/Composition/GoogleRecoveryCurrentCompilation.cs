using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>Composition-owned production checks shared by recovery; no queue initialization or ordinary approval publication.</summary>
internal sealed class GoogleRecoveryCurrentCompilation(GoogleTtsStudioPreflightEvidenceResolver preflightResolver,
    GoogleTtsStudioRequestIntentFactory intents, GoogleTtsStudioTrustContextFactory trust,
    GoogleGenerationProductionCompileAndPrepareService compiler, V222ControlSet controls, TimeProvider clock)
    : IGoogleRecoveryCurrentCompilation
{
    public async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken)
        => await CompileCoreAsync(selection, null, cancellationToken).ConfigureAwait(false);

    public Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileArticleReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, ChirpResolvedSegment source, CancellationToken cancellationToken)
    {
        var markers = ChirpArticlePlanCompiler.SentenceMarkersForPart(source.ExactText, source.Proposal, source.Part);
        return CompileCoreAsync(selection with { ExactText = source.ExactText }, markers, cancellationToken);
    }

    private async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileCoreAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, GoogleSentenceMarkers? markers, CancellationToken cancellationToken)
    {
        var preflight = await preflightResolver.ResolveAsync(selection, cancellationToken).ConfigureAwait(false);
        var capability = GoogleTtsStudioCapabilityProjection.Create(selection, preflight);
        var intent = await intents.CreateAsync(selection, capability, cancellationToken).ConfigureAwait(false);
        var options = intent.CompilationOptions with { SentenceMarkers = markers };
        var compilation = GoogleSpeechPlanCompiler.Compile(intent.Plan, options);
        string key = markers is null ? intent.IdempotencyKey : "studio-google-tts:" + ChirpPolicyValidation.Hash(new { intent.IdempotencyKey, markers, compilation.PayloadSha256 });
        intent = intent with { CompilationOptions = options, IdempotencyKey = key };
        var estimate = GoogleTtsStudioCostEstimator.Estimate(controls, selection.VoiceStableId, compilation.Payload, preflight.Account.Region, options);
        var clean = new GoogleGenerationPersistedQueueState(intent.AccountId, GoogleGenerationProvider.SynthesizeOperationStableId,
            key, false, null); // Preview only: never persisted, published for ordinary approval or dispatched.
        var now = clock.GetUtcNow();
        var evidence = new GoogleGenerationProductionCompileEvidence
        {
            Plan = intent.Plan,
            CompilationOptions = options,
            Account = preflight.Account,
            Capabilities = capability,
            PricingProvenanceId = preflight.PricingProvenanceId,
            RequestRevision = intent.RequestRevision,
            ProjectId = intent.ProjectId,
            ModelId = intent.ModelId,
            IdempotencyKey = key,
            AdmittedTrust = trust.Create(selection, intent, preflight.Account, capability, preflight.PricingProvenanceId, now),
            PreviousState = clean,
            CurrentState = clean,
            ResolutionEvidence = GoogleGenerationReconciliationResolutionEvidence.None,
            AccountAuthorized = preflight.Production.Account.IsEnabled,
            ProjectAuthorized = true,
            CapabilityCurrent = true,
            PricingCurrent = true,
            AdmissionCurrent = true,
            AccountCredentialAvailable = true,
            PricingApproved = false,
            PostCompileLimitsSatisfied = false,
            Currency = estimate.Currency,
            Scale = estimate.Scale,
            CurrentEstimateMinorUnits = estimate.MaximumMinorUnits,
            NowUtc = now,
        };
        return await compiler.CompileForReviewAsync(evidence, cancellationToken).ConfigureAwait(false);
    }

}
