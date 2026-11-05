using CloudScribe.App.ViewModels;

namespace CloudScribe.App.Composition;

/// <summary>Trusted composition-owned current compilation. Callers cannot supply authorization flags through the recovery UI.</summary>
public interface IGoogleRecoveryCurrentCompilation
{
    Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken);

    Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileArticleReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, CloudScribe.Infrastructure.Generation.ChirpResolvedSegment source,
        CancellationToken cancellationToken)
        => source.Proposal.InputFormat is null && (source.Proposal.Refinement?.SentenceMarkers is null || source.Part.CoverageOrdinal != source.Proposal.Refinement.FailedCoverageOrdinal)
            ? CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken)
            : throw new InvalidOperationException("This compilation adapter cannot attest the typed sentence format.");
}
