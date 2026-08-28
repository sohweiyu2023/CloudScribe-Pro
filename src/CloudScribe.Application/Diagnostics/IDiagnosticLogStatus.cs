namespace CloudScribe.Application.Diagnostics;

public interface IDiagnosticLogStatus
{
    event EventHandler? StatusChanged;

    bool IsAvailable { get; }

    string LogDirectory { get; }

    string CurrentLogPath { get; }

    long DroppedRecordCount { get; }
}
