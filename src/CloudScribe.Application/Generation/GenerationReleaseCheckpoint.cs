namespace CloudScribe.Application.Generation;

public enum GenerationReleaseCheckpointState
{
    PublishedPendingVerification = 0,
    Finalized = 1,
}

public sealed record GenerationReleaseCheckpoint(
    Guid CollectionId,
    long Revision,
    string ReceiptSha256,
    string OutputSha256,
    GenerationReleaseCheckpointState State,
    DateTimeOffset RecordedAtUtc)
{
    public static GenerationReleaseCheckpoint FromReceipt(
        GenerationReleaseReceipt receipt,
        GenerationReleaseCheckpointState state,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Verify())
        {
            throw new InvalidDataException("Cannot checkpoint an invalid release receipt.");
        }

        if (recordedAtUtc == default)
        {
            throw new ArgumentException("Checkpoint timestamp is required.", nameof(recordedAtUtc));
        }

        return new GenerationReleaseCheckpoint(
            receipt.CollectionId,
            receipt.Revision,
            receipt.ReceiptSha256.ToLowerInvariant(),
            receipt.OutputSha256.ToLowerInvariant(),
            state,
            recordedAtUtc.ToUniversalTime());
    }

    public void EnsureMatches(GenerationReleaseReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Verify())
        {
            throw new InvalidDataException("Release receipt failed integrity verification during checkpoint recovery.");
        }

        if (CollectionId != receipt.CollectionId ||
            Revision != receipt.Revision ||
            !string.Equals(ReceiptSha256, receipt.ReceiptSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(OutputSha256, receipt.OutputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Release checkpoint identity does not match the recovered release receipt.");
        }
    }

    public GenerationReleaseCheckpoint MarkFinalized(
        GenerationReleaseReceipt receipt,
        GenerationReleaseVerificationResult verification,
        DateTimeOffset recordedAtUtc)
    {
        EnsureMatches(receipt);
        ArgumentNullException.ThrowIfNull(verification);
        if (!verification.IsValid)
        {
            throw new InvalidOperationException("A release cannot be finalized without successful disk verification.");
        }

        if (recordedAtUtc < RecordedAtUtc)
        {
            throw new InvalidOperationException("Release checkpoint time cannot move backwards.");
        }

        return this with
        {
            State = GenerationReleaseCheckpointState.Finalized,
            RecordedAtUtc = recordedAtUtc.ToUniversalTime(),
        };
    }
}
