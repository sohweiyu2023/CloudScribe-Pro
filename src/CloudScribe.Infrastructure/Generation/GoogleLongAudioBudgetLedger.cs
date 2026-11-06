namespace CloudScribe.Infrastructure.Generation;

/// <summary>Installer-created private ledger. Runtime never creates a missing ledger or resets its history.</summary>
public sealed record GoogleLongAudioBudgetLedger(int Version, string ScopeSha256, IReadOnlyList<GoogleLongAudioBudgetClaim> Claims);
