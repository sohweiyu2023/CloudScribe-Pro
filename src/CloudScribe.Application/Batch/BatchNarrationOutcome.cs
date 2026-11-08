namespace CloudScribe.Application.Batch;

/// <summary>Result of narrating one file. <see cref="ReasonCode"/> is a fixed code (no article text, paths or provider wording).</summary>
public sealed record BatchNarrationOutcome(
    BatchOutcomeKind Kind,
    string ReasonCode,
    string? OutputPath = null,
    long OutputBytes = 0,
    string? OutputSha256 = null,
    long EstimatedMaximumMinorUnits = 0,
    long ReservedMinorUnits = 0)
{
    public static BatchNarrationOutcome Completed(string path, long bytes, string sha256, long estimated = 0, long reserved = 0) =>
        new(BatchOutcomeKind.Completed, "batch.item-complete", path, bytes, sha256, estimated, reserved);
}
