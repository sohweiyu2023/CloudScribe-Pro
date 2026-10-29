namespace CloudScribe.Application.Generation;

/// <summary>Caller-proposed coverage. The production evidence store must independently verify the receipt.</summary>
public sealed record ChirpNarrationReuse(int StartScalar, int EndScalarExclusive, string TextSha256, string ReceiptSha256);
