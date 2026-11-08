using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using CloudScribe.Infrastructure.Pricing;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    /// <summary>Real capture -> independent evidence -> compile -> explicit approval chain, with disposable stores.</summary>
    private sealed class MatrixPipeline
    {
        internal GoogleGenerationProductionPendingApprovalStateOwner Pending { get; } = new();
        internal GoogleGenerationProductionSubmissionStateOwner Submission { get; }
        internal GoogleGenerationSpendAuthorizationStore Spend { get; }
        internal GoogleGenerationPersistedQueueStateStore Queue { get; }
        internal GoogleGenerationProductionRuntimeRequestSource Runtime { get; }
        internal int CanarySubmissions { get; private set; }
        private readonly GoogleTtsStudioCaptureCoordinator _capture;
        private readonly GoogleGenerationProductionIntentAssemblyCoordinator _assembly;
        private readonly GoogleGenerationProductionPreparationCoordinator _preparation;
        private readonly GoogleGenerationProductionSpendApprovalService _approval;
        private readonly EfNarrationCopyStore _copies;

        internal MatrixPipeline(CatalogReopenFixture f)
        {
            var controls = new V222ControlSet(new StrictJsonObjectReader());
            var pricing = new EfPricingCatalogHistoryStore(f.Factory, f.Clock);
            var evidence = new GoogleGenerationProductionEvidenceResolver(f.Accounts, f.Capabilities, f.Clock);
            var accountFactory = new GoogleGenerationProductionAccountFactory(f.Clock, new GoogleServiceAccountCredentialOnboardingService(f.Vault));
            var preflight = new GoogleTtsStudioPreflightEvidenceResolver(evidence, accountFactory, pricing, f.Vault, controls, f.Clock);
            var intents = new GoogleGenerationProductionRequestIntentStateOwner();
            var authority = new GoogleGenerationProductionAuthorizationSnapshotStateOwner();
            var current = new GoogleGenerationProductionCurrentRequestStateOwner();
            Queue = new(f.Factory);
            Spend = new(f.Factory, f.Clock);
            Submission = new(Spend);
            Runtime = new(Spend, Submission.ResolveCurrentAsync);
            _capture = new(preflight, new(f.Authorizations, f.Clock), new(controls), new(Queue), intents, authority, controls, f.Clock);
            _assembly = new(intents, current, new GoogleGenerationProductionIntentEvidenceResolver(authority,
                evidence, accountFactory, f.Authorizations, f.Vault, pricing, f.Clock));
            _preparation = new(current, new(new(Pending), evidence));
            _approval = new(Pending, Submission);
            _copies = new(f.Factory, new(f.Disk.Paths), f.Clock);
        }

        internal void Bind(ShellViewModel model)
        {
            model.ConfigureNarrationCopies(_copies);
            model.ConfigureGoogleRequestInvalidation(() => { Pending.Invalidate(); Submission.Invalidate(); });
            model.ConfigureStage6GoogleGenerationPreparation(async token =>
            {
                await _capture.CaptureAsync(model.CaptureGoogleTtsStudioRequestSelection(), token).ConfigureAwait(false);
                await _assembly.AssembleCurrentAsync(token).ConfigureAwait(false);
                var pending = await _preparation.PrepareCurrentAsync(token).ConfigureAwait(false);
                return new ShellViewModel.GoogleGenerationSpendReview(pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits,
                    pending.Envelope.PricingProvenanceId, pending.Envelope.VoiceName, pending.Envelope.CompiledPayloadSha256).Validate();
            });
            model.ConfigureStage6GoogleGenerationSpendApproval((maximum, confirmed, token) => _approval.ApproveExplicitAsync(new(maximum, confirmed), token));
            BindCanaryExecution(model);
        }

        private void BindCanaryExecution(ShellViewModel model)
        {
            // A reachable fake submission canary, not a missing execution delegate that makes zero sends vacuous.
            var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
            {
                CanarySubmissions++;
                return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.RejectedSafeToRetry, null,
                    ReadOnlyMemory<byte>.Empty, null, null, "google-http-400", GoogleErrorStatus.InvalidArgument));
            });
            var bound = new GoogleGenerationBoundQueueCoordinator(queue, Queue.LoadAsync, Queue.SaveAsync,
                Queue.TryClaimSubmissionAsync, Queue.SaveOutcomeAsync);
            model.ConfigureStage6GoogleGeneration(async token =>
            {
                var request = await Runtime.ResolveAsync(token).ConfigureAwait(false);
                return new GoogleGenerationUiExecutionContext(new(bound), request.Snapshot, request.SubmissionEnvelope);
            });
            model.ConfigureStage6GoogleGenerationOutput((_, _) => throw new InvalidOperationException("Synthetic canary cannot publish audio."),
                (_, _) => throw new InvalidOperationException("Synthetic canary cannot play audio."));
        }
    }
}
