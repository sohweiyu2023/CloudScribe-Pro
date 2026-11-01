using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

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
    private readonly Func<bool>? catalogExpired;
    private readonly Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection;
    private ShellViewModel.GoogleTtsStudioRequestSelection _selection;

    internal ChirpWholeArticleRuntime(ChirpArticlePreparationService preparation, ChirpArticleService articles,
        GoogleGenerationProductionRuntimeRequestSource requests,
        Func<GoogleGenerationProductionRuntimeRequest, CancellationToken, Task<GoogleGenerationUiExecutionContext>> resolveExecution,
        ArticleAudioExportService exporter, ShellViewModel.GoogleTtsStudioRequestSelection selection, string destination, string ffmpeg,
        Func<bool>? catalogExpired = null, Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection = null)
    {
        this.preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        this.articles = articles ?? throw new ArgumentNullException(nameof(articles));
        this.requests = requests ?? throw new ArgumentNullException(nameof(requests));
        this.resolveExecution = resolveExecution ?? throw new ArgumentNullException(nameof(resolveExecution));
        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        this.destination = destination; this.ffmpeg = ffmpeg;
        this.catalogExpired = catalogExpired; this.refreshSelection = refreshSelection;
    }

    public ChirpWholeArticleRuntime(ChirpArticlePreparationService preparation, ChirpArticleService articles,
        GoogleGenerationProductionRuntimeRequestSource requests, GoogleGenerationProductionExecutionContextResolver resolver,
        ArticleAudioExportService exporter, ShellViewModel.GoogleTtsStudioRequestSelection selection, string destination, string ffmpeg,
        Func<bool>? catalogExpired = null, Func<CancellationToken, Task<ShellViewModel.GoogleTtsStudioRequestSelection>>? refreshSelection = null)
        : this(preparation, articles, requests, resolver.ResolveAsync, exporter, selection, destination, ffmpeg, catalogExpired, refreshSelection) { }

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
            || !string.Equals(updated.LanguageCode, _selection.LanguageCode, StringComparison.Ordinal))
            throw new InvalidOperationException("Refreshed evidence changed the approved article, account, project or exact voice. Stop without switching.");
        _selection = updated;
    }

    public async Task GenerateAsync(ChirpWholeArticleJob job, int ordinal, CancellationToken cancellationToken)
    {
        if (_selection.DocumentId != job.DocumentId) throw new InvalidOperationException("The article job selection changed.");
        var review = await preparation.PrepareAsync(_selection, ordinal, cancellationToken).ConfigureAwait(false);
        await preparation.ApproveWholeArticleRequestAsync(_selection, job.JobId, review.PreviewId, cancellationToken).ConfigureAwait(false);
        var request = await requests.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var context = await resolveExecution(request, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(context.Snapshot, request.Snapshot))
            throw new InvalidOperationException("The resolved execution context differs from the exact durably authorized article request.");
        var state = context.Snapshot;
        var outcome = await context.Coordinator.ProcessPersistedTransitionAsync(
            state.UiSelection, state.AccountAuthorized, state.ProjectAuthorized, state.CapabilityCurrent,
            state.PricingCurrent, state.ProviderRequest, state.AdmittedTrust, state.PreviousState,
            state.CurrentState, state.ResolutionEvidence, state.AdmissionCurrent,
            state.AccountCredentialAvailable, state.PricingApproved, state.PostCompileLimitsSatisfied,
            cancellationToken).ConfigureAwait(false);
        if (outcome.RequiresReconciliation || outcome.Response is not { IsAccepted: true })
            throw new InvalidOperationException("Article request stopped. Inspect its existing safe provider diagnostic and retained hold; no automatic retry.");
        using var localSave = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await articles.PersistAcceptedAsync(state.ProviderRequest, outcome, localSave.Token).ConfigureAwait(false);
    }

    public async Task<ChirpWholeArticleOutput> ExportAsync(ChirpWholeArticleJob job, CancellationToken cancellationToken)
    {
        await exporter.ExportAsync(job.DocumentId, destination, ffmpeg, cancellationToken).ConfigureAwait(false);
        using var stream = File.OpenRead(destination);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new(Path.GetFullPath(destination), stream.Length, Convert.ToHexStringLower(hash));
    }
}
