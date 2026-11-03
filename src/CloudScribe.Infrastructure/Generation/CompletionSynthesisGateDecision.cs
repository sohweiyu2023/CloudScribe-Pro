namespace CloudScribe.Infrastructure.Generation;

/// <summary>Derived from pinned bridge readback, never caller spending authority.</summary>
public sealed record CompletionSynthesisGateDecision(bool RequireProtectedCapture, string? IdempotencyKeySha256);
