namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task RunLongAudioOperationAsync(Guid documentId, Func<CancellationToken, Task> operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnterGoogleGenerationOperation();
        try
        {
            if (IsDocumentDirty || IsDocumentSaving || CurrentDocumentId != documentId)
                throw new InvalidOperationException("Save and select the reviewed document before Long Audio generation.");
            InvalidatePrivateGoogleDiagnostic();
            PreparedGoogleGenerationSpendApproved = false; PreparedGoogleGenerationSpendReview = null;
            await operation(token).ConfigureAwait(true);
        }
        finally { ExitGoogleGenerationOperation(); }
    }
}
