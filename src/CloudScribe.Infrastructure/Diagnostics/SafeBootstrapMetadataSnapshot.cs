namespace CloudScribe.Infrastructure.Diagnostics;

public sealed record SafeBootstrapMetadataSnapshot(string EventName, IReadOnlyDictionary<string, string> Fields, bool FieldsComplete,
    IReadOnlyList<SafeStartupPhaseObservation> StartupPhases);
