using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Shared by the real private modal and lifecycle tests; readonly compile grants no spend authority.</summary>
public sealed class GooglePrivateDiagnosticOptIn(GooglePrivateErrorDiagnostics diagnostics, IGoogleRecoveryCurrentCompilation compilation)
{
    public async Task<PrivateErrorCaptureBinding?> PrepareAsync(ShellViewModel model, Func<bool> stillConfirmed, CancellationToken token)
    {
        if (!stillConfirmed()) return null;
        long review = diagnostics.BeginOptInReview();
        long version = model.PrivateDiagnosticRequestVersion;
        var selection = model.CaptureGoogleTtsStudioRequestSelection();
        if (selection.RevisionId is null || model.IsDocumentDirty || model.IsDocumentSaving)
            throw new InvalidOperationException("Save the selected document first.");
        var compiled = await compilation.CompileReadonlyAsync(selection, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        if (version != model.PrivateDiagnosticRequestVersion || model.IsDocumentDirty || model.IsDocumentSaving)
            throw new InvalidOperationException("Request changed during diagnostic opt-in.");
        var envelope = compiled.Envelope;
        if (!string.Equals(envelope.AccountId, selection.AccountStableId, StringComparison.Ordinal)
            || !string.Equals(envelope.VoiceName, selection.VoiceStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Readonly diagnostic compilation identity changed.");
        var binding = new PrivateErrorCaptureBinding(selection.DocumentId, selection.RevisionId.Value, version, envelope.AccountId,
            selection.ProjectStableId, envelope.VoiceName, envelope.CredentialReferenceId, envelope.CompiledPayloadSha256);
        return diagnostics.CompleteOptInReview(review, binding, stillConfirmed()) ? binding : null;
    }
}
