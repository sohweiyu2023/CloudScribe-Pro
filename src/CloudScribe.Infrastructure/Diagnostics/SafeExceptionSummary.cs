namespace CloudScribe.Infrastructure.Diagnostics;

public sealed record SafeExceptionSummary(
    int SchemaVersion,
    SafeExceptionCategory Category,
    int ErrorCode,
    int? NativeErrorCode,
    int? SqlitePrimaryCode,
    int? SqliteExtendedCode,
    SafeExceptionStackState StackState,
    IReadOnlyList<SafeExceptionLocation> Locations,
    IReadOnlyList<SafeExceptionCause> InnerCauses,
    bool Truncated,
    bool MetadataIncomplete);
