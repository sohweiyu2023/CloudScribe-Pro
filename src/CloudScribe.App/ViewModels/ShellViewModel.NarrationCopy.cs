using CloudScribe.Application.Batch;
using CloudScribe.Application.Documents;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    private INarrationCopyStore? _narrationCopies;
    private int _narrationCopyInFlight;

    public void ConfigureNarrationCopies(INarrationCopyStore? store) => _narrationCopies = store;

    public async Task<NarrationCopyPreparation> UseNarrationCopyAsync(string previewedOriginal)
    {
        EnsureDocumentWorkflowConfigured();
        var store = _narrationCopies ?? throw new InvalidOperationException("Narration copies are unavailable in this build.");
        if (!HasOpenDocument || !string.Equals(DocumentText, previewedOriginal, StringComparison.Ordinal))
            throw new InvalidOperationException("The source changed. Preview it again.");
        if (Interlocked.CompareExchange(ref _narrationCopyInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A narration copy is already being prepared.");
        Guid sourceId = _currentDocumentId!.Value;
        try
        {
            if (IsDocumentDirty && !await SaveCurrentDocumentCoreAsync("Before creating a narration copy").ConfigureAwait(true))
                throw new InvalidOperationException("Wait for your edits to save, then preview again.");
            if (_currentDocumentId != sourceId || !string.Equals(DocumentText, previewedOriginal, StringComparison.Ordinal) || _currentRevisionId is not Guid revision)
                throw new InvalidOperationException("The selected source changed. Preview it again.");
            var copy = await store.PrepareAsync(sourceId, revision, BatchNarrationIdentity.Hash(previewedOriginal)).ConfigureAwait(true);
            if (_currentDocumentId != sourceId || !string.Equals(DocumentText, previewedOriginal, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected source changed. The saved copy is in Library; it was not opened.");
            if (copy.Narration.Id != sourceId)
            {
                ApplyDocumentSnapshot(copy.Narration, $"Narration copy opened: {copy.OmittedUrlCount} web address(es) skipped. Your original is unchanged.");
                await RefreshDocumentLibraryCoreAsync().ConfigureAwait(true);
            }
            return copy;
        }
        finally { Volatile.Write(ref _narrationCopyInFlight, 0); }
    }

    private async Task<GoogleGenerationUiExecutionContext> ResolveNarrationExecutionContextAsync(CancellationToken token)
    {
        var context = await ResolveGoogleExecutionContextAsync(token).ConfigureAwait(true);
        var request = context.Snapshot.ProviderRequest;
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(request.CompiledPayload.Span));
        bool reviewedPayload = string.Equals(PreparedGoogleGenerationSpendReview?.CompiledPayloadSha256, hash, StringComparison.Ordinal);
        bool selectedIdentity = SelectedVoiceLabVoice is { } voice
            && string.Equals(context.Snapshot.UiSelection.AccountId, voice.AccountStableId, StringComparison.Ordinal)
            && string.Equals(context.Snapshot.UiSelection.ProjectId, voice.ProjectStableId, StringComparison.Ordinal)
            && string.Equals(context.Snapshot.UiSelection.VoiceId, voice.VoiceStableId, StringComparison.Ordinal);
        bool sample = _googleSamplePrepared && reviewedPayload && selectedIdentity;
        var envelope = context.SubmissionEnvelope;
        bool separatelyAuthorized = reviewedPayload && selectedIdentity && envelope is not null && (envelope.RecoveryAuthorizationId is not null || envelope.ChirpAuthorizationId is not null)
            && string.Equals(envelope.AccountId, request.AccountId, StringComparison.Ordinal)
            && string.Equals(envelope.VoiceName, context.Snapshot.UiSelection.VoiceId, StringComparison.Ordinal)
            && envelope.CompiledPayloadBytes == request.CompiledPayload.Length
            && string.Equals(envelope.CompiledPayloadSha256, hash, StringComparison.Ordinal);
        if (!sample && !separatelyAuthorized) await ValidateCurrentNarrationCopyAsync(token, includePreparedSample: true).ConfigureAwait(true);
        return context;
    }

    private async Task ValidateCurrentNarrationCopyAsync(CancellationToken token, bool includePreparedSample = false)
    {
        if (_narrationCopies is null || CurrentDocumentId is not Guid documentId || _googleSampleCapture is not null || _googleSamplePrepared && !includePreparedSample) return;
        try
        {
            var copy = await _narrationCopies.FindAsync(documentId, token).ConfigureAwait(true);
            if (copy is null) return;
            if (IsDocumentDirty || IsDocumentSaving || CurrentDocumentId != copy.Narration.Id || CurrentRevisionId != copy.Narration.CurrentRevisionId
                || !string.Equals(DocumentText, copy.Narration.Text, StringComparison.Ordinal))
                throw new InvalidOperationException("The narration copy changed. Open the original and prepare a fresh narration copy before review.");
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        {
            ClearChangedGoogleRequest();
            CloudScribe.Application.Logging.BatchDiagnostics.Source(_logger, null, documentId, CloudScribe.Application.Logging.BatchSourceOutcome.NarrationInvalidated);
            throw;
        }
    }
}
