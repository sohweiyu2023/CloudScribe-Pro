namespace CloudScribe.Infrastructure.Diagnostics;

internal sealed record SafeDiagnosticMetadataSnapshot(
    string EventName, string Component, string Message,
    IReadOnlyDictionary<string, string?> Fields, bool FieldsComplete);
