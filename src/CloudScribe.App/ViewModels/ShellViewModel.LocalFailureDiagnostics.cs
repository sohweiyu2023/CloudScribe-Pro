using CloudScribe.Application.Documents;
using CloudScribe.Application.Logging;

namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    // Observability is advisory: preserve the editor, original failure, save ordering and close guard.
    private void ObserveLocalDocumentFailure(LocalRequestStage stage, Exception error) => CloudScribeLog.Observe(() =>
    {
        if (error is DocumentConcurrencyException) LocalRequestDiagnostics.Mark(error, LocalRequestReason.DocumentConflict);
        _ = LocalRequestDiagnostics.Failure(_logger, Guid.NewGuid(), stage, error, DateTimeOffset.UtcNow);
    });
}
