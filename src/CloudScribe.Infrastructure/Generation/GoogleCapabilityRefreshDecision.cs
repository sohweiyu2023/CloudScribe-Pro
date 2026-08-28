namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleCapabilityRefreshDecision(
    bool Accepted,
    string DiagnosticCode,
    GoogleCapabilitySnapshot? Snapshot);
