namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Code identity only: no method names, filenames, argument values or memory addresses.</summary>
public sealed record SafeExceptionLocation(int CauseIndex, Guid ModuleId, int MethodToken, int IlOffset);
