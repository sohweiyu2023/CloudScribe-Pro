namespace CloudScribe.Infrastructure.Generation;

/// <summary>Verified installed identities and canonical profile locations; never initializes money or submits a request.</summary>
public sealed record InstalledGoogleLongAudioContext(string ParentPolicyPath, string ParentPolicySha256,
    string ScopePath, string ScopeSha256, string LedgerPath, string OperationsDirectory, string OutputDirectory,
    GoogleLongAudioBudgetScope Scope, IGoogleLongAudioScopeExtensionSource? Extension = null);
