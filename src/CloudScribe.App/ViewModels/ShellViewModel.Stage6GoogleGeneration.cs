using System.Security.Cryptography;
using CloudScribe.App.Navigation;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>>? _resolveGoogleGenerationExecutionContext;
    private Func<CancellationToken, Task<GoogleGenerationSpendReview>>? _prepareGoogleGenerationForApproval;
    private Func<long, bool, CancellationToken, Task>? _approveGoogleGenerationSpend;
    private Func<GoogleGenerationQueueOutcome, CancellationToken, Task<string>>? _persistAcceptedGoogleMp3;
    private Func<string, CancellationToken, Task>? _playVerifiedGoogleMp3;
    private GoogleGenerationSpendReview? _preparedGoogleGenerationSpendReview;
    private bool _preparedGoogleGenerationSpendApproved;
    private string? _lastGeneratedGoogleMp3Path;
    private int _googleGenerationInFlight;
    private Func<CloudScribe.Application.Documents.DocumentSnapshot, CloudScribe.Providers.Abstractions.GenerationProviderRequest,
        GoogleGenerationQueueOutcome, string, CancellationToken, Task>? _recordArticleAudio;

    public event EventHandler? ArticleExportRequested;

    [RelayCommand]
    private void RequestArticleExport() => ArticleExportRequested?.Invoke(this, EventArgs.Empty);

    public void ConfigureArticleAudioRecording(Func<CloudScribe.Application.Documents.DocumentSnapshot,
        CloudScribe.Providers.Abstractions.GenerationProviderRequest, GoogleGenerationQueueOutcome,
        string, CancellationToken, Task> record) => _recordArticleAudio = record;

    public bool CanGenerateWithGoogle =>
        _resolveGoogleGenerationExecutionContext is not null &&
        _persistAcceptedGoogleMp3 is not null &&
        PreparedGoogleGenerationSpendApproved &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public bool CanPrepareGoogleGenerationSpend =>
        _prepareGoogleGenerationForApproval is not null &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public bool CanApproveGoogleGenerationSpend =>
        _approveGoogleGenerationSpend is not null &&
        PreparedGoogleGenerationSpendReview is not null &&
        !PreparedGoogleGenerationSpendApproved &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public GoogleGenerationSpendReview? PreparedGoogleGenerationSpendReview
    {
        get => _preparedGoogleGenerationSpendReview;
        private set
        {
            if (!SetProperty(ref _preparedGoogleGenerationSpendReview, value))
                return;
            OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
        }
    }

    public bool PreparedGoogleGenerationSpendApproved
    {
        get => _preparedGoogleGenerationSpendApproved;
        private set
        {
            if (!SetProperty(ref _preparedGoogleGenerationSpendApproved, value))
                return;
            OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
            OnPropertyChanged(nameof(CanGenerateWithGoogle));
            GenerateWithGoogleCommand.NotifyCanExecuteChanged();
        }
    }

    public string? LastGeneratedGoogleMp3Path
    {
        get => _lastGeneratedGoogleMp3Path;
        private set
        {
            if (!SetProperty(ref _lastGeneratedGoogleMp3Path, value))
                return;
            OnPropertyChanged(nameof(CanPlayLastGeneratedGoogleMp3));
            PlayLastGeneratedGoogleMp3Command.NotifyCanExecuteChanged();
        }
    }

    public bool CanPlayLastGeneratedGoogleMp3 =>
        _playVerifiedGoogleMp3 is not null &&
        !string.IsNullOrWhiteSpace(LastGeneratedGoogleMp3Path) &&
        Volatile.Read(ref _googleGenerationInFlight) == 0;

    public void ConfigureStage6GoogleGeneration(
        Func<CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveCurrentExecutionContext)
    {
        _resolveGoogleGenerationExecutionContext = resolveCurrentExecutionContext
            ?? throw new ArgumentNullException(nameof(resolveCurrentExecutionContext));
        RefreshGoogleGenerationCommands();
        RefreshGoogleGenerationRouteAction();
    }

    public void ConfigureStage6GoogleGenerationPreparation(
        Func<CancellationToken, Task<GoogleGenerationSpendReview>> prepareCurrentRequestForApproval)
    {
        _prepareGoogleGenerationForApproval = prepareCurrentRequestForApproval
            ?? throw new ArgumentNullException(nameof(prepareCurrentRequestForApproval));
        RefreshGoogleGenerationCommands();
    }

    public void ConfigureStage6GoogleGenerationSpendApproval(
        Func<long, bool, CancellationToken, Task> approveExplicitSpend)
    {
        _approveGoogleGenerationSpend = approveExplicitSpend
            ?? throw new ArgumentNullException(nameof(approveExplicitSpend));
        RefreshGoogleGenerationCommands();
    }

    public void ConfigureStage6GoogleGenerationOutput(
        Func<GoogleGenerationQueueOutcome, CancellationToken, Task<string>> persistAcceptedMp3,
        Func<string, CancellationToken, Task> playVerifiedMp3)
    {
        _persistAcceptedGoogleMp3 = persistAcceptedMp3
            ?? throw new ArgumentNullException(nameof(persistAcceptedMp3));
        _playVerifiedGoogleMp3 = playVerifiedMp3
            ?? throw new ArgumentNullException(nameof(playVerifiedMp3));
        RefreshGoogleGenerationCommands();
    }

    public async Task<GoogleGenerationSpendReview> PrepareGoogleGenerationSpendReviewAsync(
        CancellationToken cancellationToken = default)
    {
        long requestVersion = Volatile.Read(ref _googleRequestVersion);
        EnterGoogleGenerationOperation();
        try
        {
            var prepare = _prepareGoogleGenerationForApproval
                ?? throw new InvalidOperationException("Google generation production preparation is not configured.");
            PreparedGoogleGenerationSpendReview = null;
            PreparedGoogleGenerationSpendApproved = false;
            _googleSamplePrepared = false;
            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = "Google generation · compiling exact current request for spend review";
            GoogleGenerationSpendReview review = await prepare(cancellationToken).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Google generation preparation returned no spend review.");
            review.Validate();
            RequireUnchangedGoogleRequest(requestVersion);
            PreparedGoogleGenerationSpendReview = review;
            StatusMessage = "Google generation · exact compiled estimate ready for explicit review";
            return review;
        }
        finally
        {
            ExitGoogleGenerationOperation();
        }
    }

    public async Task ApproveGoogleGenerationSpendAsync(
        long authorizedMaximumMinorUnits,
        bool confirmedByUser,
        CancellationToken cancellationToken = default)
    {
        long requestVersion = Volatile.Read(ref _googleRequestVersion);
        EnterGoogleGenerationOperation();
        try
        {
            GoogleGenerationSpendReview review = PreparedGoogleGenerationSpendReview
                ?? throw new InvalidOperationException("Prepare the exact current Google request and review its estimate before approving spend.");
            review.Validate();
            if (PreparedGoogleGenerationSpendApproved)
                throw new InvalidOperationException("The currently prepared Google request is already spend-approved.");
            if (authorizedMaximumMinorUnits < review.CurrentEstimateMinorUnits)
            {
                throw new InvalidOperationException(
                    $"Authorized spend ceiling is below the exact compiled estimate of {review.CurrentEstimateMinorUnits} minor units.");
            }
            var approve = _approveGoogleGenerationSpend
                ?? throw new InvalidOperationException("Google generation explicit spend approval is not configured.");
            cancellationToken.ThrowIfCancellationRequested();
            StatusMessage = "Google generation · confirming spend authorization for the displayed compiled request";
            await approve(authorizedMaximumMinorUnits, confirmedByUser, cancellationToken).ConfigureAwait(true);
            RequireUnchangedGoogleRequest(requestVersion);
            PreparedGoogleGenerationSpendApproved = true;
            StatusMessage = "Google generation · displayed exact compiled spend authorized";
        }
        finally
        {
            ExitGoogleGenerationOperation();
        }
    }

    private void RefreshGoogleGenerationRouteAction()
    {
        if (!_pages.TryGetValue(AppRoute.Studio, out RoutePageViewModel? page) || page is null)
            return;
        if (_resolveGoogleGenerationExecutionContext is null)
            return;
        // The toolbar's guided Create MP3 window owns the visible request review.
        // A direct route action here bypassed that explanation and appeared disabled on return.
        page.HasPrimaryAction = false;
    }

    [RelayCommand(CanExecute = nameof(CanGenerateWithGoogle))]
    private async Task GenerateWithGoogleAsync(CancellationToken cancellationToken)
    {
        EnterGoogleGenerationOperation();
        try
        {
            Guid? articleIdAtStart = _currentDocumentId;
            Guid? articleRevisionAtStart = _currentRevisionId;
            long articleRequestVersion = Volatile.Read(ref _googleRequestVersion);
            bool articleCleanAtStart = !IsDocumentDirty && !IsDocumentSaving;
            GoogleGenerationUiExecutionContext executionContext = await ResolveGoogleExecutionContextAsync(cancellationToken)
                .ConfigureAwait(true);
            var coordinator = executionContext.Coordinator
                ?? throw new InvalidOperationException("Google generation coordinator is unavailable for the current authorization state.");
            var state = executionContext.Snapshot
                ?? throw new InvalidOperationException("Google generation UI state is unavailable.");
            var articleDocument = articleCleanAtStart && articleIdAtStart is { } documentId && _documentLibrary is not null
                ? await _documentLibrary.OpenAsync(documentId, cancellationToken).ConfigureAwait(true) : null;
            if (articleIdAtStart != _currentDocumentId || articleRevisionAtStart != _currentRevisionId
                || articleDocument?.CurrentRevisionId != articleRevisionAtStart || IsDocumentDirty || IsDocumentSaving
                || articleRequestVersion != Volatile.Read(ref _googleRequestVersion)) articleDocument = null;
            StatusMessage = "Google generation · validating current account, voice, pricing and trust";
            if (_logger.IsEnabled(LogLevel.Information))
            {
                string payloadHash = Convert.ToHexStringLower(SHA256.HashData(state.ProviderRequest.CompiledPayload.Span));
                CloudScribeLog.GoogleGenerationRequestContext(_logger, payloadHash, state.ProviderRequest.CompiledPayload.Length);
            }
            AttachPrivateGoogleDiagnostic(state.ProviderRequest, articleRequestVersion);
            GoogleGenerationQueueOutcome outcome = await coordinator.ProcessPersistedTransitionAsync(
                state.UiSelection, state.AccountAuthorized, state.ProjectAuthorized, state.CapabilityCurrent,
                state.PricingCurrent, state.ProviderRequest, state.AdmittedTrust, state.PreviousState,
                state.CurrentState, state.ResolutionEvidence, state.AdmissionCurrent,
                state.AccountCredentialAvailable, state.PricingApproved, state.PostCompileLimitsSatisfied,
                cancellationToken).ConfigureAwait(true);
            await HandleGoogleGenerationOutcomeAsync(outcome, state.ProviderRequest, cancellationToken).ConfigureAwait(true);
            await RecordArticleAudioOutcomeAsync(articleDocument, state.ProviderRequest, outcome, cancellationToken).ConfigureAwait(true);
            InvalidatePrivateGoogleDiagnostic();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CloudScribeLog.GoogleGenerationStopped(_logger, "caller-cancelled");
            StatusMessage = "Google generation cancelled · approval cleared; check queue status before another request";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            // A failure may occur after dispatch. Keep the durable queue authoritative and do
            // not imply that repeating the request is free or safe. Provider exception text can
            // contain private request details, so it does not belong in the visible status.
            CloudScribeLog.GoogleGenerationStopped(_logger, "guard-or-execution-failure");
            StatusMessage = "Google generation stopped · approval cleared; review queue status before preparing another request";
        }
        finally
        {
            // Spend approval is single-use at the desktop boundary. Whether the guarded Stage6
            // transition succeeds, rejects, or requires reconciliation, another provider attempt
            // must be freshly prepared and explicitly approved against its exact compiled bytes.
            InvalidatePrivateGoogleDiagnostic();
            PreparedGoogleGenerationSpendApproved = false;
            PreparedGoogleGenerationSpendReview = null;
            ExitGoogleGenerationOperation();
        }
    }

    private async Task RecordArticleAudioOutcomeAsync(CloudScribe.Application.Documents.DocumentSnapshot? articleDocument,
        CloudScribe.Providers.Abstractions.GenerationProviderRequest request, GoogleGenerationQueueOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_recordArticleAudio is not null && articleDocument is not null && string.Equals(outcome.Response?.DiagnosticCode, "google-accepted", StringComparison.Ordinal)
            && !request.IdempotencyKey.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal)
            && !outcome.RequiresReconciliation && LastGeneratedGoogleMp3Path is { } acceptedPath)
        {
            try { await _recordArticleAudio(articleDocument, request, outcome, acceptedPath, cancellationToken).ConfigureAwait(true); }
            catch (Exception error) when (!IsFatalUiException(error))
            {
                CloudScribeLog.GoogleGenerationStopped(_logger, "article-receipt-unavailable");
                StatusMessage = "Accepted MP3 saved; article provenance could not be recorded. Keep the audio; do not regenerate to repair export.";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlayLastGeneratedGoogleMp3))]
    private async Task PlayLastGeneratedGoogleMp3Async(CancellationToken cancellationToken)
    {
        try
        {
            string path = LastGeneratedGoogleMp3Path
                ?? throw new InvalidOperationException("No verified Google MP3 output is available.");
            var play = _playVerifiedGoogleMp3
                ?? throw new InvalidOperationException("Google MP3 playback is not configured.");
            await play(path, cancellationToken).ConfigureAwait(true);
            StatusMessage = $"Google generation · opened verified MP3 · {path}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Google MP3 playback cancelled";
        }
        catch (Exception exception) when (!IsFatalUiException(exception))
        {
            StatusMessage = "Google MP3 playback unavailable · the verified output could not be opened; no new generation was started";
        }
    }

    private async Task<GoogleGenerationUiExecutionContext> ResolveGoogleExecutionContextAsync(
        CancellationToken cancellationToken)
    {
        var resolveContext = _resolveGoogleGenerationExecutionContext
            ?? throw new InvalidOperationException("Google generation execution context resolution is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        return await resolveContext(cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Google generation execution context is unavailable for the current authorization state.");
    }

    private async Task HandleGoogleGenerationOutcomeAsync(
        GoogleGenerationQueueOutcome outcome,
        CloudScribe.Providers.Abstractions.GenerationProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string diagnostic = GoogleGenerationOutcomeDiagnostic.Sanitize(outcome.Response?.DiagnosticCode);
        GoogleErrorStatus errorStatus = outcome.Response is { } response && Enum.IsDefined(response.ErrorStatus)
            ? response.ErrorStatus : GoogleErrorStatus.Unknown;
        CloudScribeLog.GoogleGenerationOutcome(_logger, diagnostic, outcome.RequiresReconciliation,
            outcome.Response?.IsAccepted == true, errorStatus);
        string errorLabel = errorStatus == GoogleErrorStatus.NotProvided ? string.Empty : $" · {errorStatus}";
        if (outcome.RequiresReconciliation)
        {
            StatusMessage = $"Google generation · {diagnostic} · reconciliation required; no output exposed. Keep the reservation; do not resubmit.";
            return;
        }
        if (outcome.Response?.IsAccepted != true)
        {
            StatusMessage = $"Google generation · not accepted · {diagnostic}{errorLabel}. Charge is not confirmed; keep the reservation and review request status before retrying.";
            StatusMessage += GoogleValidationGuidance.For(diagnostic, errorStatus,
                outcome.Response?.ValidationHint ?? GoogleValidationMessageHint.NotProvided);
            return;
        }
        if (string.Equals(diagnostic, "google-accepted-invalid-media", StringComparison.Ordinal))
        {
            StatusMessage = "Google accepted the request but returned unusable audio. Keep the reservation; do not generate it again to recover the output.";
            return;
        }
        var persist = _persistAcceptedGoogleMp3
            ?? throw new InvalidOperationException("Verified Google MP3 persistence is not configured.");
        LastGeneratedGoogleMp3Path = request.IdempotencyKey.StartsWith(ChirpArticleService.KeyPrefix, StringComparison.Ordinal)
            ? await (_persistAcceptedChirpMp3 ?? throw new InvalidOperationException("Durable internal article output is unavailable."))(request, outcome, cancellationToken).ConfigureAwait(true)
            : await persist(outcome, cancellationToken).ConfigureAwait(true);
        StatusMessage = $"Google generation · verified MP3 ready · {LastGeneratedGoogleMp3Path}";
    }

    private void EnterGoogleGenerationOperation()
    {
        if (Interlocked.CompareExchange(ref _googleGenerationInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A Google generation approval or submission is already in progress.");
        RefreshGoogleGenerationCommands();
    }

    private void ExitGoogleGenerationOperation()
    {
        Volatile.Write(ref _googleGenerationInFlight, 0);
        RefreshGoogleGenerationCommands();
    }

    private void RefreshGoogleGenerationCommands()
    {
        OnPropertyChanged(nameof(CanGenerateWithGoogle));
        OnPropertyChanged(nameof(CanPrepareGoogleGenerationSpend));
        OnPropertyChanged(nameof(CanApproveGoogleGenerationSpend));
        OnPropertyChanged(nameof(CanPlayLastGeneratedGoogleMp3));
        GenerateWithGoogleCommand.NotifyCanExecuteChanged();
        PlayLastGeneratedGoogleMp3Command.NotifyCanExecuteChanged();
    }

    public sealed record GoogleGenerationSpendReview(
        string Currency,
        int Scale,
        long CurrentEstimateMinorUnits,
        string PricingProvenanceId,
        string VoiceName,
        string CompiledPayloadSha256)
    {
        public GoogleGenerationSpendReview Validate()
        {
            if (string.IsNullOrWhiteSpace(Currency)
                || string.IsNullOrWhiteSpace(PricingProvenanceId)
                || string.IsNullOrWhiteSpace(VoiceName)
                || string.IsNullOrWhiteSpace(CompiledPayloadSha256))
            {
                throw new InvalidOperationException("Google generation spend review is missing exact compiled request evidence.");
            }
            if (Scale is < 0 or > 9)
                throw new InvalidOperationException("Google generation spend review currency scale must be between zero and nine.");
            if (CurrentEstimateMinorUnits < 0)
                throw new InvalidOperationException("Google generation spend review estimate cannot be negative.");
            return this;
        }
    }
}
