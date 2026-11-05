namespace CloudScribe.Infrastructure.Generation;

/// <summary>
/// Trusted integration boundary, deliberately without a production implementation or registration.
/// Must consume exact independent spend/lineage authority and verify current credential/project,
/// pricing, requested voice/format compatibility, and an existing private output bucket's permissions
/// with a fresh, unoccupied output object. No existing remote object may be overwritten.
/// Approval must never be inferred from proposal fields or from this adapter's journal.
/// </summary>
public interface IGoogleLongAudioAdmission
{
    ValueTask RequireSubmissionAuthorizedAsync(GoogleLongAudioRequest request, CancellationToken cancellationToken);
}
