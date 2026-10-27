namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact baseline row identity and digest, independently admitted rather than inferred after failure.</summary>
public sealed record RecoveryFinancialRowPin(string Identity, string Sha256);
