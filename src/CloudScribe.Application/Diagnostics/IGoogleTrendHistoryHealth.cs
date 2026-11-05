namespace CloudScribe.Application.Diagnostics;

/// <summary>Durability health of permanent, schema-filtered Google request trend metadata, independent of private response retention.</summary>
public interface IGoogleTrendHistoryHealth
{
    bool IsAvailable { get; }
    string FailureReason { get; }
    int? LastFailureHResult { get; }
    bool HasPartialTail { get; }
    long RecordsWritten { get; }
    void RequireReady();
}
