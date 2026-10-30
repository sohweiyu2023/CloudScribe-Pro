namespace CloudScribe.Infrastructure.Generation;

/// <summary>Fixed local pre-dispatch failure. Does not authorize resetting a consumed claim or retrying.</summary>
public sealed class PrivateDiagnosticNotReadyException() : InvalidOperationException(
    "Private diagnostics are not ready for this exact diagnostic successor. Enable explicit one-response capture after reservation and inspect retained request state before any separately approved submission.");
