using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

/// <summary>Shared by the real private modal and lifecycle tests; readonly compile grants no spend authority.</summary>
public sealed class GooglePrivateDiagnosticOptIn(GooglePrivateErrorDiagnostics diagnostics, IGoogleRecoveryCurrentCompilation compilation)
{
    public async Task<PrivateErrorCaptureBinding?> PrepareAsync(ShellViewModel model, Func<bool> stillConfirmed, CancellationToken token)
    {
        bool enabled = false;
        try
        {
            if (!stillConfirmed()) return null;
            long review = diagnostics.BeginOptInReview();
            long version = model.PrivateDiagnosticRequestVersion;
            var selection = model.CaptureGoogleTtsStudioRequestSelection();
            if (selection.RevisionId is null || model.IsDocumentDirty || model.IsDocumentSaving)
                throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Save the selected document first."), LocalRequestReason.CaptureUnsaved);
            var compiled = await compilation.CompileReadonlyAsync(selection, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (version != model.PrivateDiagnosticRequestVersion || model.IsDocumentDirty || model.IsDocumentSaving)
                throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Request changed during diagnostic opt-in."), LocalRequestReason.CaptureSelectionChanged);
            var envelope = compiled.Envelope;
            if (!string.Equals(envelope.AccountId, selection.AccountStableId, StringComparison.Ordinal)
                || !string.Equals(envelope.VoiceName, selection.VoiceStableId, StringComparison.Ordinal))
                throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Readonly diagnostic compilation identity changed."), LocalRequestReason.CaptureIdentityChanged);
            var binding = new PrivateErrorCaptureBinding(selection.DocumentId, selection.RevisionId.Value, version, envelope.AccountId,
                selection.ProjectStableId, envelope.VoiceName, envelope.CredentialReferenceId, envelope.CompiledPayloadSha256);
            enabled = diagnostics.CompleteOptInReview(review, binding, stillConfirmed());
            return enabled ? binding : null;
        }
        finally { if (!enabled) diagnostics.Disarm(); }
    }
}
