namespace CloudScribe.Domain.Generation;

public sealed record GenerationRetryDecision(
    bool MayRetryAutomatically,
    TimeSpan Delay,
    string Reason)
{
    public static GenerationRetryDecision Blocked(string reason) => new(false, TimeSpan.Zero, reason);
}
