using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

/// <summary>Trusted composition-owned current compilation. Callers cannot supply authorization flags through the recovery UI.</summary>
public interface IGoogleRecoveryCurrentCompilation
{
    Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken);
}
