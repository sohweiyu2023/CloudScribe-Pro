using CloudScribe.App.ViewModels;
using CloudScribe.Application.Diagnostics;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App.Composition;

/// <summary>Uses the same exact compiler, durable spend reader, queue coordinator and accepted-output journal as individual requests.</summary>
public sealed class ChirpWholeArticleRuntime : IChirpWholeArticleRuntime
{
    private readonly ChirpArticlePreparationService preparation;
    private readonly ChirpArticleService articles;
    private readonly GoogleGenerationProductionRuntimeRequestSource requests;
    private readonly Func<GoogleGenerationProductionRuntimeRequest, CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveExecution;
    private readonly ArticleAudioExportService exporter;
    private readonly string destination;
    private readonly string ffmpeg;
    private readonly TrustedMediaToolSelection? approvedMediaTools;
    private readonly Func<bool>? catalogExpired;
    private readonly Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection;
    private readonly PrivateArticleErrorCaptureScope? privateCapture;
    private readonly ILogger? diagnosticLogger;
    private readonly Action<ArticleExecutionStage, int>? observeStage;
    private readonly IGoogleTrendHistoryHealth? trendHistory;
    private ShellViewModel.GoogleTtsStudioRequestSelection _selection;

    internal ChirpWholeArticleRuntime(ChirpArticlePreparationService preparation, ChirpArticleService articles,
        GoogleGenerationProductionRuntimeRequestSource requests,
        Func<GoogleGenerationProductionRuntimeRequest, CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveExecution,
        ArticleAudioExportService exporter, ShellViewModel.GoogleTtsStudioRequestSelection selection, string destination, string ffmpeg,
        Func<bool>? catalogExpired = null, Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection = null,
        PrivateArticleErrorCaptureScope? privateCapture = null, ILogger? diagnosticLogger = null,
        Action<ArticleExecutionStage, int>? observeStage = null, IGoogleTrendHistoryHealth? trendHistory = null, TrustedMediaToolSelection? approvedMediaTools = null)
    {
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.articles = articles ?? throw new ArgumentNullException(nameof(articles));
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.resolveExecution = resolveExecution ?? throw new ArgumentNullException(nameof(resolveExecution));
        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        this.destination = destination; this.ffmpeg = ffmpeg;
        this.catalogExpired = catalogExpired; this.refreshSelection = refreshSelection;
        this.privateCapture = privateCapture;
        this.diagnosticLogger = diagnosticLogger; this.observeStage = observeStage;
        this.trendHistory = trendHistory; this.approvedMediaTools = approvedMediaTools;
    }

    public ChirpWholeArticleRuntime(ChirpArticlePreparationService preparation, ChirpArticleService articles,
        GoogleGenerationProductionRuntimeRequestSource requests, IGoogleGenerationExecutionContextResolver resolver,
        ArticleAudioExportService exporter, ShellViewModel.GoogleTtsStudioRequestSelection selection, string destination, string ffmpeg,
        Func<bool>? catalogExpired = null, Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection = null,
        PrivateArticleErrorCaptureScope? privateCapture = null, ILogger? diagnosticLogger = null,
        Action<ArticleExecutionStage, int>? observeStage = null, IGoogleTrendHistoryHealth? trendHistory = null, TrustedMediaToolSelection? approvedMediaTools = null)
        : this(preparation, articles, requests, resolver.ResolveAsync, exporter, selection, destination, ffmpeg, catalogExpired, refreshSelection, privateCapture, diagnosticLogger, observeStage, trendHistory, approvedMediaTools) { }

    public bool RequiresCatalogRefresh => catalogExpired?.Invoke() == true;

    public async Task RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        var updated = await (refreshSelection ?? throw new InvalidOperationException("No supported catalog refresh is configured."))(cancellationToken).ConfigureAwait(false);
        if (updated.DocumentId != _selection.DocumentId || updated.RevisionId != _selection.RevisionId
            || !string.Equals(updated.ExactText, _selection.ExactText, StringComparison.Ordinal)
            || !string.Equals(updated.AccountStableId, _selection.AccountStableId, StringComparison.Ordinal)
            || !string.Equals(updated.ProjectStableId, _selection.ProjectStableId, StringComparison.Ordinal)
            || !string.Equals(updated.VoiceStableId, _selection.VoiceStableId, StringComparison.Ordinal)
            || !string.Equals(updated.VoiceFingerprint, _selection.VoiceFingerprint, StringComparison.Ordinal)
            || !string.Equals(updated.LanguageCode, _selection.LanguageCode, StringComparison.Ordinal)
            || !string.Equals(updated.ProviderStableId, _selection.ProviderStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Refreshed evidence changed the approved article, account, project or exact voice. Stop without switching.");
        if (updated.CapturedAtUtc <= _selection.CapturedAtUtc
            || string.Equals(updated.CapabilityEvidenceId, _selection.CapabilityEvidenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("Catalog verification did not advance the exact selection evidence. No speech request may start.");
        _selection = updated;
    }

    public Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
        => GenerateCoreAsync(job, ordinal, null, cancellationToken);

    public Task GenerateRecoveryAsync(ChirpWholeArticleJob job, ChirpArticleRecoveryChild child, CancellationToken cancellationToken)
        => GenerateCoreAsync(job, child.CoverageOrdinal, child, cancellationToken);

    private async Task GenerateCoreAsync(ChirpWholeArticleJob job, int ordinal, ChirpArticleRecoveryChild? recovery, CancellationToken cancellationToken)
    {
        bool acceptedAndSaved = false;
        bool captureKnownRejection = false;
        bool outcomeObserved = false, providerAccepted = false;
        var providerStatus = GoogleErrorStatus.NotProvided;
        var stage = ArticleExecutionStage.Preparing;
        void Enter(ArticleExecutionStage value) { stage = value; ObserveOperation(job, ordinal, value, ArticleExecutionOutcome.Started, outcomeObserved, providerAccepted, providerStatus); }
        try
        {
            Enter(ArticleExecutionStage.Preparing);
            RequireTrendHistoryReady();
            privateCapture?.RequireReady(job);
            RequireSelectedDocument(job);
            // Recheck after rehearsal and before every fresh or recovery reservation. Existing accepted parts never enter here.
            await ArticleAudioExportService.PreflightGenerationAsync(destination, ffmpeg, approvedMediaTools, cancellationToken).ConfigureAwait(false);
            await PreparePartAsync(job, ordinal, recovery, Enter, cancellationToken).ConfigureAwait(false);
            Enter(ArticleExecutionStage.Resolving);
            var request = await requests.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
            ObservePartIdentity(job, ordinal, request, recovery?.Depth ?? 0);
            if (recovery is null) privateCapture?.Attach(job, ordinal, request.Snapshot.ProviderRequest, request.SubmissionEnvelope);
            else privateCapture?.AttachRecovery(job, recovery, request.Snapshot.ProviderRequest, request.SubmissionEnvelope);
            var context = await resolveExecution(request, CancellationToken.None).ConfigureAwait(false);
            RequireSameSnapshot(context.Snapshot, request.Snapshot);
            var state = context.Snapshot;
            Enter(ArticleExecutionStage.Provider);
            RequireTrendHistoryReady();
            var outcome = await context.Coordinator.ProcessPersistedTransitionAsync(
                state.UiSelection, state.AccountAuthorized, state.ProjectAuthorized, state.CapabilityCurrent,
                state.PricingCurrent, state.ProviderRequest, state.AdmittedTrust, state.PreviousState,
                state.CurrentState, state.ResolutionEvidence, state.AdmissionCurrent,
                state.AccountCredentialAvailable, state.PricingApproved, state.PostCompileLimitsSatisfied,
                CancellationToken.None).ConfigureAwait(false);
            (outcomeObserved, providerAccepted, providerStatus) = (outcome.Response is not null, outcome.Response?.IsAccepted == true,
                outcome.Response?.ErrorStatus ?? GoogleErrorStatus.NotProvided);
            captureKnownRejection = job.MaximumRecoveryRequests > 0 && !outcome.RequiresReconciliation && outcome.Response is
            {
                Disposition: SubmissionDisposition.NotSubmitted, DiagnosticCode: "google-http-400", ErrorStatus: GoogleErrorStatus.InvalidArgument,
                ValidationHint: GoogleValidationMessageHint.SentenceLengthReported
            };
            if (outcome.RequiresReconciliation || outcome.Response is not { IsAccepted: true })
                throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Article provider outcome stopped; retain its exact diagnostic and hold."),
                    outcome.RequiresReconciliation ? LocalRequestReason.ProviderOutcomeUncertain : LocalRequestReason.ProviderRejected);
            Enter(ArticleExecutionStage.AcceptedOutputSave);
            await articles.PersistAcceptedAsync(state.ProviderRequest, outcome, CancellationToken.None).ConfigureAwait(false);
            acceptedAndSaved = true;
            ObserveOperation(job, ordinal, stage, ArticleExecutionOutcome.Completed, outcomeObserved, providerAccepted, providerStatus);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (error is PrivateDiagnosticNotReadyException) LocalRequestDiagnostics.Mark(error, LocalRequestReason.CaptureEvidenceRequired);
            ObserveOperation(job, ordinal, stage, error is OperationCanceledException ? ArticleExecutionOutcome.Cancelled : ArticleExecutionOutcome.Failed,
                outcomeObserved, providerAccepted, providerStatus); throw;
        }
        finally
        {
            if (privateCapture is not null) await privateCapture.CompleteAsync(acceptedAndSaved, CancellationToken.None, captureKnownRejection).ConfigureAwait(false);
        }
    }

    private void RequireSelectedDocument(ChirpWholeArticleJob job)
    {
        if (_selection.DocumentId != job.DocumentId) throw new InvalidOperationException("The article job selection changed.");
    }

    private static void RequireSameSnapshot(object actual, object expected)
    {
        if (!ReferenceEquals(actual, expected))
            throw new InvalidOperationException("The resolved execution context differs from the exact durably authorized article request.");
    }

    private void RequireTrendHistoryReady()
    {
        try { trendHistory?.RequireReady(); }
        catch (InvalidOperationException error) { throw LocalRequestDiagnostics.Mark(error, LocalRequestReason.TrendHistoryUnavailable); }
    }

    private async Task PreparePartAsync(ChirpWholeArticleJob job, int ordinal, ChirpArticleRecoveryChild? recovery,
        Action<ArticleExecutionStage> enter, CancellationToken token)
    {
        if (recovery is null)
        {
            await PrepareAndApproveFreshPartAsync(job, ordinal, enter, token).ConfigureAwait(false);
        }
        else
        {
            token.ThrowIfCancellationRequested();
            enter(ArticleExecutionStage.Reservation);
            RequireTrendHistoryReady();
            await preparation.PrepareAndApproveRecoveryAsync(_selection, job, recovery, token,
                freshToken => RefreshPreparationSelectionAsync(job, ordinal, recovery, freshToken)).ConfigureAwait(false);
        }
    }

    private async Task PrepareAndApproveFreshPartAsync(ChirpWholeArticleJob job, int ordinal,
        Action<ArticleExecutionStage> enter, CancellationToken token)
    {
        ChirpRequestReview? staleReview = null;
        try
        {
            staleReview = await preparation.PrepareAsync(_selection, ordinal, token).ConfigureAwait(false);
            await ApproveFreshPartAsync(job, staleReview, enter, token).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (LocalRequestDiagnostics.Describe(error, DateTimeOffset.UtcNow).Reason == LocalRequestReason.CapabilityStale)
        {
            // ApprovePreparedAsync recompiles read-only before reserving. Prove that boundary
            // was not crossed before discarding its stale review or refreshing anything.
            await articles.RequireUnreservedCatalogRefreshAsync(job, ordinal, null, token).ConfigureAwait(false);
            if (staleReview is not null) preparation.Discard(staleReview.PreviewId);
            await RefreshPreparationSelectionAsync(job, ordinal, null, token).ConfigureAwait(false);
            var renewed = await preparation.PrepareAsync(_selection, ordinal, token).ConfigureAwait(false);
            // One replacement review only. A second expiry or any later failure stops normally.
            await ApproveFreshPartAsync(job, renewed, enter, token).ConfigureAwait(false);
        }
    }

    private async Task ApproveFreshPartAsync(ChirpWholeArticleJob job, ChirpRequestReview review,
        Action<ArticleExecutionStage> enter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        enter(ArticleExecutionStage.Reservation);
        RequireTrendHistoryReady();
        await preparation.ApproveWholeArticleRequestAsync(_selection, job.JobId, review.PreviewId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ShellViewModel.GoogleTtsStudioRequestSelection> RefreshPreparationSelectionAsync(
        ChirpWholeArticleJob job, int ordinal, ChirpArticleRecoveryChild? child, CancellationToken token)
    {
        await articles.RequireUnreservedCatalogRefreshAsync(job, ordinal, child, token).ConfigureAwait(false);
        await RefreshCatalogAsync(token).ConfigureAwait(false);
        if (RequiresCatalogRefresh) throw new InvalidOperationException("Catalog verification made no forward freshness progress.");
        return _selection;
    }

    private void ObservePartIdentity(ChirpWholeArticleJob job, int ordinal, GoogleGenerationProductionRuntimeRequest request, int depth)
    {
        if (diagnosticLogger is null || !CloudScribeLog.ObserveIsEnabled(diagnosticLogger, LogLevel.Information)) return;
        string keyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Snapshot.ProviderRequest.IdempotencyKey)));
        CloudScribeLog.Observe(() => CloudScribeLog.ArticleRequestIdentity(diagnosticLogger,
            job.JobId, ordinal, keyHash, request.SubmissionEnvelope.CompiledPayloadSha256));
        if (request.SubmissionEnvelope.ChirpAuthorizationId is { } authorization)
            CloudScribeLog.Observe(() => CloudScribeLog.ArticlePartProvenance(diagnosticLogger, job.JobId, ordinal,
                authorization, keyHash, request.SubmissionEnvelope.CompiledPayloadSha256, depth));
    }

    public async Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
    {
        var stage = ArticleExecutionStage.Assembly;
        ObserveOperation(job, 0, stage, ArticleExecutionOutcome.Started, true, true, GoogleErrorStatus.NotProvided);
        try
        {
            if (approvedMediaTools is not null)
                await exporter.ExportAsync(job.DocumentId, destination, approvedMediaTools, cancellationToken).ConfigureAwait(false);
            else await exporter.ExportAsync(job.DocumentId, destination, ffmpeg, cancellationToken).ConfigureAwait(false);
            stage = ArticleExecutionStage.OutputVerification;
            ObserveOperation(job, 0, stage, ArticleExecutionOutcome.Started, true, true, GoogleErrorStatus.NotProvided);
            using var stream = File.OpenRead(destination);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            ObserveOperation(job, 0, stage, ArticleExecutionOutcome.Completed, true, true, GoogleErrorStatus.NotProvided);
            return new(Path.GetFullPath(destination), stream.Length, Convert.ToHexStringLower(hash));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { ObserveOperation(job, 0, stage, error is OperationCanceledException ? ArticleExecutionOutcome.Cancelled : ArticleExecutionOutcome.Failed, true, true, GoogleErrorStatus.NotProvided); throw; }
    }

    private void ObserveOperation(ChirpWholeArticleJob job, int ordinal, ArticleExecutionStage stage, ArticleExecutionOutcome outcome,
        bool observed, bool accepted, GoogleErrorStatus errorStatus)
    {
        CloudScribeLog.Observe(() => observeStage?.Invoke(stage, ordinal));
        if (diagnosticLogger is not null) CloudScribeLog.Observe(() => CloudScribeLog.ArticleOperation(diagnosticLogger,
            job.JobId, ordinal, stage, outcome, observed, accepted, errorStatus));
    }
}
