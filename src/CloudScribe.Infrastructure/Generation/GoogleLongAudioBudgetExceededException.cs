namespace CloudScribe.Infrastructure.Generation;

/// <summary>A local pre-dispatch funding stop, not a provider rejection or billing settlement.</summary>
public sealed class GoogleLongAudioBudgetExceededException(long provisionUsdCents, long capUsdCents)
    : InvalidOperationException(FormattableString.Invariant($"Long Audio funding limit reached: the required provision is US${provisionUsdCents / 100m:F2}, above the approved US${capUsdCents / 100m:F2} cap. No new synthesis request was sent by this action. Saved audio and all previous holds are preserved. Review funding before resuming this saved job; do not create a duplicate."))
{
    public long ProvisionUsdCents { get; } = provisionUsdCents;
    public long CapUsdCents { get; } = capUsdCents;
}
