using System.Globalization;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Logging;
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
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The exact internal request is no longer eligible for private observation."), LocalRequestReason.ReservedRequestIneligible);
        string[] parts = active.Snapshot.ProviderRequest.IdempotencyKey.Split(':');
        if (parts.Length is not (3 or 4) || parts.Length == 4 && (!Guid.TryParseExact(parts[3], "N", out var keyAuthorization)
                || keyAuthorization != active.SubmissionEnvelope.ChirpAuthorizationId)
            || !string.Equals(parts[0] + ":", ChirpArticleService.KeyPrefix, StringComparison.Ordinal)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Internal request identity is invalid."), LocalRequestReason.ReservedIdentityMalformed);
        var source = await ResolveSelectionAsync(selection, ordinal, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(source.Proposal.PlanSha256, parts[1], StringComparison.Ordinal))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("The selected saved article differs from the reserved internal request."), LocalRequestReason.ReservedSourceChanged);
        var current = await compilation.CompileReadonlyAsync(selection with { ExactText = source.ExactText }, cancellationToken).ConfigureAwait(false);
        if (!current.Snapshot.ProviderRequest.CompiledPayload.Span.SequenceEqual(active.Snapshot.ProviderRequest.CompiledPayload.Span))
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Private diagnostics cannot observe changed request bytes."), LocalRequestReason.ReservedPayloadChanged);
        if ((current.Envelope with { ChirpAuthorizationId = active.SubmissionEnvelope.ChirpAuthorizationId }) != active.SubmissionEnvelope)
        {
            bool revisionOnly = (current.Envelope with
            {
                ChirpAuthorizationId = active.SubmissionEnvelope.ChirpAuthorizationId,
                RequestRevision = active.SubmissionEnvelope.RequestRevision,
            }) == active.SubmissionEnvelope;
            // Comparison only: never submit or persist the normalized envelope.
            throw LocalRequestDiagnostics.Mark(new InvalidOperationException("Private diagnostics cannot observe a changed reserved envelope."),
                revisionOnly ? LocalRequestReason.ReservedRevisionChanged : LocalRequestReason.ReservedEnvelopeChanged);
        }
        return current;
    }
}
