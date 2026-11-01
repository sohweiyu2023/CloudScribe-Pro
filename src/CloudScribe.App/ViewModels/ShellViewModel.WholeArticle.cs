namespace CloudScribe.App.ViewModels;

public sealed partial class ShellViewModel
{
    public async Task RunWholeArticleOperationAsync(Func<GoogleTtsStudioRequestSelection, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnterGoogleGenerationOperation();
        try
        {
            if (IsDocumentDirty || IsDocumentSaving) throw new InvalidOperationException("Save the original article before Generate.");
            InvalidatePrivateGoogleDiagnostic(); // Article jobs never arm sensitive capture implicitly.
            PreparedGoogleGenerationSpendApproved = false;
            PreparedGoogleGenerationSpendReview = null;
            await operation(CaptureGoogleTtsStudioRequestSelection(), cancellationToken).ConfigureAwait(true);
        }
        finally { ExitGoogleGenerationOperation(); }
    }
}
