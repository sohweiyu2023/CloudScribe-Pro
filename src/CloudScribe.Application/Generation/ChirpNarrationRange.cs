namespace CloudScribe.Application.Generation;

/// <summary>A proposal range, not a receipt, durable identity, admission or spend authority.</summary>
public sealed record ChirpNarrationRange(int StartScalar, int EndScalarExclusive, string TextSha256, string? ReusedReceiptSha256);
