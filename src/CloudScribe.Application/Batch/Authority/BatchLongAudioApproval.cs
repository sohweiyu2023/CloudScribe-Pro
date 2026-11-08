namespace CloudScribe.Application.Batch.Authority;

/// <summary>The exact Long Audio request a batch approval admits for one file: the attempt identity and output object
/// (both part of the request binding), the binding itself (which covers the exact saved text), the request ceiling, and
/// the most the file may commit including any adaptive splitting. All amounts are US cents, like the installed scope.</summary>
public sealed record BatchLongAudioApproval(
    Guid AttemptId,
    string OutputGcsUri,
    string RequestBindingSha256,
    long CeilingUsdCents,
    long FamilyMaximumUsdCents,
    long MaximumOutputBytes,
    int ExpectedSampleRate,
    int ExpectedChannels,
    bool AdaptiveRecovery);
