namespace CloudScribe.Infrastructure.Generation;

/// <summary>Production owner must freshly resolve exact account/project/credential/storage-read budget and existing durable claim.
/// The journal, request object and this interface do not establish financial or lineage authority.</summary>
public interface IGoogleLongAudioOutputAuthority
{
    ValueTask<GoogleLongAudioOutputAllowance> RequireReadAuthorizedAsync(GoogleLongAudioRequest request,
        GoogleLongAudioCheckpoint checkpoint, CancellationToken cancellationToken);
}
