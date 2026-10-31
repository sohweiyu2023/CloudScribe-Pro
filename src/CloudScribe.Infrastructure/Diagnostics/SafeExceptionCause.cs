namespace CloudScribe.Infrastructure.Diagnostics;

public sealed record SafeExceptionCause(SafeExceptionCategory Category, int ErrorCode);
