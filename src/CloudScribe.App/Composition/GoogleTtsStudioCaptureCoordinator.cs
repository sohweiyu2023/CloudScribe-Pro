using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.App.Composition;

/// <summary>Connects the visible Studio selection to the existing independently validated Stage6 pipeline.</summary>
internal sealed class GoogleTtsStudioCaptureCoordinator(
    GoogleTtsStudioPreflightEvidenceResolver preflightResolver,
    GoogleTtsStudioRequestIntentFactory intentFactory,
    GoogleTtsStudioTrustContextFactory trustFactory,
    GoogleTtsStudioQueueStateResolver queueResolver,
    GoogleGenerationProductionRequestIntentStateOwner intentOwner,
    GoogleGenerationProductionAuthorizationSnapshotStateOwner authorizationOwner,
    V222ControlSet controls,
    TimeProvider timeProvider)
{
    public async Task CaptureAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken)
    {
        var preflight = await preflightResolver.ResolveAsync(selection, cancellationToken).ConfigureAwait(false);
        GoogleCapabilitySnapshot capability = GoogleTtsStudioCapabilityProjection.Create(selection, preflight);
        var intent = await intentFactory.CreateAsync(selection, capability, cancellationToken).ConfigureAwait(false);
        var queue = await queueResolver.ResolveAsync(intent, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var trust = trustFactory.Create(selection, intent, preflight.Account, capability, preflight.PricingProvenanceId, now);
        var compilation = GoogleSpeechPlanCompiler.Compile(intent.Plan, intent.CompilationOptions);
        capability.RequireSupported(intent.CompilationOptions.VoiceName, intent.CompilationOptions.AudioEncoding, compilation.Payload.Length, now);
        var estimate = GoogleTtsStudioCostEstimator.Estimate(controls, selection.VoiceStableId, compilation.Payload, preflight.Account.Region);
        cancellationToken.ThrowIfCancellationRequested();
        intentOwner.PublishCoherentCapture(intent, authorizationOwner, new()
        {
            AccountId = intent.AccountId,
            ProjectId = intent.ProjectId,
            ModelId = intent.ModelId,
            IdempotencyKey = intent.IdempotencyKey,
            Account = preflight.Account,
            Capabilities = capability,
            PricingProvenanceId = preflight.PricingProvenanceId,
            RequestRevision = intent.RequestRevision,
            AdmittedTrust = trust,
            PreviousState = queue.PreviousState,
            CurrentState = queue.CurrentState,
            ResolutionEvidence = queue.ResolutionEvidence,
            AccountAuthorized = preflight.Production.Account.IsEnabled,
            ProjectAuthorized = false, // Re-resolved independently by the production evidence resolver.
            CapabilityCurrent = !preflight.Production.Capability.IsStale(now),
            PricingCurrent = true, // Exact active/authenticated catalog binding was checked by preflight.
            AdmissionCurrent = true, // Preflight, live credential availability, capability and queue checks all completed.
            AccountCredentialAvailable = true,
            PricingApproved = false,
            PostCompileLimitsSatisfied = false,
            Currency = estimate.Currency,
            Scale = estimate.Scale,
            CurrentEstimateMinorUnits = estimate.MaximumMinorUnits,
            CapturedAtUtc = now,
        });
    }
}
