using System.Globalization;
using CloudScribe.App.ViewModels;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.App.Composition;

public sealed partial class ChirpArticlePreparationService
{
    /// <summary>Private diagnostics observe the exact already reserved slice. No approval, queue mutation or dispatch is performed.</summary>
    public async Task<GoogleGenerationProductionPendingApprovalStateOwner.PendingState> CompileReadonlyAsync(
        ShellViewModel.GoogleTtsStudioRequestSelection selection, CancellationToken cancellationToken)
    {
        var active = await state.ResolveCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (active?.SubmissionEnvelope.ChirpAuthorizationId is null)
            return await compilation.CompileReadonlyAsync(selection, cancellationToken).ConfigureAwait(false);
        if (await articles.LoadSpendAsync(active.SubmissionEnvelope, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("The exact internal request is no longer eligible for private observation.");
        string[] parts = active.Snapshot.ProviderRequest.IdempotencyKey.Split(':');
        if (parts.Length is not (3 or 4) || parts.Length == 4 && (!Guid.TryParseExact(parts[3], "N", out var keyAuthorization)
                || keyAuthorization != active.SubmissionEnvelope.ChirpAuthorizationId)
            || !string.Equals(parts[0] + ":", ChirpArticleService.KeyPrefix, StringComparison.Ordinal)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal))
            throw new InvalidOperationException("Internal request identity is invalid.");
        var source = await ResolveSelectionAsync(selection, ordinal, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(source.Proposal.PlanSha256, parts[1], StringComparison.Ordinal))
            throw new InvalidOperationException("The selected saved article differs from the reserved internal request.");
        var current = await compilation.CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken).ConfigureAwait(false);
        if ((current.Envelope with { ChirpAuthorizationId = active.SubmissionEnvelope.ChirpAuthorizationId }) != active.SubmissionEnvelope
            || !current.Snapshot.ProviderRequest.CompiledPayload.Span.SequenceEqual(active.Snapshot.ProviderRequest.CompiledPayload.Span))
            throw new InvalidOperationException("Private diagnostics cannot observe stale or changed request bytes.");
        return current;
    }
}
