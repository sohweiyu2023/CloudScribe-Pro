namespace CloudScribe.Infrastructure.Generation;

/// <summary>Retained conservative commitment, never an invoice, refund or permission to resend.</summary>
public sealed record GoogleLongAudioBudgetClaim(Guid AttemptId, string RequestBindingSha256, long MaximumUsdCents,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GoogleLongAudioRecoveryProvenance? Recovery = null);
