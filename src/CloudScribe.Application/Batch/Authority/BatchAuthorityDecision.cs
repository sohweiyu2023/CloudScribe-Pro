namespace CloudScribe.Application.Batch.Authority;

/// <summary>Result of checking a batch against its profile and consent. Explanations never contain file names or text.</summary>
public sealed record BatchAuthorityDecision(bool Allowed, string ReasonCode, string Explanation)
{
    public static BatchAuthorityDecision Deny(string reasonCode, string explanation) => new(false, reasonCode, explanation);
}
