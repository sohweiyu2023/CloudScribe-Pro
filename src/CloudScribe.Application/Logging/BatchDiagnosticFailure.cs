namespace CloudScribe.Application.Logging;

public enum BatchDiagnosticFailure
{
    Unknown, Cancelled, Timeout, AccessDenied, MissingFile, Storage, Database, MalformedData,
    InvalidData, Validation, Network, Concurrency, AlreadyRunning, SettingsChanged, NotAuthorized,
}
