using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.App.Composition;

public sealed partial class ChirpArticlePreparationService
{
    /// <summary>Uses the already confirmed bounded article grant; native code reauthenticates child ownership and every hold.</summary>
    public async Task PrepareAndApproveRecoveryAsync(ShellViewModel.GoogleTtsStudioRequestSelection selection,
        ChirpWholeArticleJob job, ChirpArticleRecoveryChild child, CancellationToken token,
        Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshStaleSelection = null)
    {
        if (child.JobId != job.JobId || selection.DocumentId != job.DocumentId
            || !string.Equals(child.PlanSha256, job.PlanSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The exact recovery job or article changed.");
        var source = await articles.ResolveRecoveryChildAsync(child, token).ConfigureAwait(false);
        RequireSourceSelection(source, selection);
        GoogleGenerationProductionPendingApprovalStateOwner.PendingState pending;
        try { pending = await compilation.CompileArticleReadonlyAsync(selection, source, token).ConfigureAwait(false); }
        catch (InvalidOperationException error) when (refreshStaleSelection is not null
            && LocalRequestDiagnostics.Describe(error, clock.GetUtcNow()).Reason == LocalRequestReason.CapabilityStale)
        {
            selection = await refreshStaleSelection(token).ConfigureAwait(false);
            RequireSourceSelection(source, selection);
            pending = await compilation.CompileArticleReadonlyAsync(selection, source, token).ConfigureAwait(false);
        }
        var intent = new ChirpRequestIntent(selection.DocumentId, child.CoverageOrdinal, selection.ProjectStableId,
            pending.Envelope, pending.Snapshot.ProviderRequest.CompiledPayload, pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits);
        token.ThrowIfCancellationRequested();
        // Once the reservation starts, reconcile it independently of a user stop request.
        var approval = await articles.ApproveRecoveryChildAsync(child, intent, CancellationToken.None).ConfigureAwait(false);
        if (approval.AuthorizationId != child.AuthorizationId)
            throw new InvalidOperationException("The durably approved child identity changed; reconcile without dispatch.");
        var envelope = pending.Envelope with { ChirpAuthorizationId = approval.AuthorizationId };
        var request = pending.Snapshot.ProviderRequest;
        var exact = new GenerationProviderRequest(request.ProviderStableId, request.OperationStableId, request.AccountId,
            approval.IdempotencyKey, request.CompiledPayload.ToArray(), request.OutputFormat);
        var clean = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, approval.IdempotencyKey, false, null);
        var snapshot = pending.Snapshot with { ProviderRequest = exact, PreviousState = clean, CurrentState = clean, PricingApproved = true };
        await state.ActivateArticleAsync(envelope, snapshot, pending.CurrentEstimateMinorUnits, CancellationToken.None).ConfigureAwait(false);
    }
}
