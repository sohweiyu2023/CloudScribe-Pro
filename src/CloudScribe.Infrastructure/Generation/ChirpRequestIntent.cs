namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpRequestIntent(Guid DocumentId, int CoverageOrdinal, string ProjectId,
    GoogleGenerationSubmissionEnvelope Envelope, ReadOnlyMemory<byte> CompiledPayload,
    string Currency, int Scale, long EstimateMinorUnits);
