namespace CloudScribe.Domain.Generation;

public sealed record GenerationMasteringProfile(
    string StableId,
    decimal TargetPeakDbfs,
    decimal? TargetLufs,
    int FadeInMilliseconds,
    int FadeOutMilliseconds)
{
    public GenerationMasteringProfile Validate()
    {
        ValidateValues(StableId, TargetPeakDbfs, TargetLufs, FadeInMilliseconds, FadeOutMilliseconds);
        return this;
    }

    private static void ValidateValues(
        string stableId,
        decimal targetPeakDbfs,
        decimal? targetLufs,
        int fadeInMilliseconds,
        int fadeOutMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        if (targetPeakDbfs is > 0 or < -30)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPeakDbfs));
        }

        if (targetLufs is > 0 or < -70)
        {
            throw new ArgumentOutOfRangeException(nameof(targetLufs));
        }

        if (fadeInMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeInMilliseconds));
        }

        if (fadeOutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeOutMilliseconds));
        }
    }
}
