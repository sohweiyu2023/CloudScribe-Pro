namespace CloudScribe.Infrastructure.Generation;

/// <summary>GET-only continuation of the exact committed, completed operation. Never approves a synthesis POST.</summary>
public sealed class GoogleLongAudioLedgerOutputAuthority(GoogleLongAudioLedgerAdmission admission,
    GoogleLongAudioFileJournal journal,
    Func<GoogleLongAudioRequest, CancellationToken, ValueTask<GoogleLongAudioOutputAllowance>> requireStorageReadiness)
    : IGoogleLongAudioOutputAuthority
{
    public async ValueTask<GoogleLongAudioOutputAllowance> RequireReadAuthorizedAsync(GoogleLongAudioRequest request,
        GoogleLongAudioCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (journal.Load(request.AttemptId) != checkpoint
            || checkpoint.AttemptId != request.AttemptId || !string.Equals(checkpoint.BindingSha256, request.BindingSha256, StringComparison.Ordinal)
            || !string.Equals(checkpoint.ProjectNumber, request.ProjectNumber, StringComparison.Ordinal) || !string.Equals(checkpoint.Location, request.Location, StringComparison.Ordinal)
            || !string.Equals(checkpoint.CredentialReferenceId, request.CredentialReferenceId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.OutputGcsUri, request.OutputGcsUri, StringComparison.Ordinal) || !string.Equals(checkpoint.PayloadSha256, request.PayloadSha256, StringComparison.Ordinal)
            || checkpoint.PayloadBytes != request.PayloadBytes || checkpoint.State != GoogleLongAudioState.CompletedUnverified
            || checkpoint.HttpStatus != 200 || checkpoint.RpcCode is not null || checkpoint.OperationParseFailed
            || checkpoint.Observation is not { ReadbackVerified: true, ReadState: CloudScribe.Domain.Generation.GoogleResponseReadState.Complete }
            || checkpoint.OperationName is null
            || !checkpoint.OperationName.StartsWith($"projects/{request.ProjectNumber}/locations/{request.Location}/operations/", StringComparison.Ordinal))
            throw new InvalidOperationException("Storage continuation requires the exact durable completed operation.");
        await admission.RequireExistingCommitmentAsync(request, cancellationToken).ConfigureAwait(false);
        var allowance = await requireStorageReadiness(request, cancellationToken).ConfigureAwait(false);
        if (journal.Load(request.AttemptId) != checkpoint)
            throw new InvalidOperationException("The completed operation changed during storage readiness verification.");
        return allowance;
    }
}
