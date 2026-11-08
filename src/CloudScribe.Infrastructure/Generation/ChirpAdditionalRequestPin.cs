namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact credential reference, capability, pricing, revision, voice, encoding, payload and estimate. No secret is stored.</summary>
public sealed record ChirpAdditionalRequestPin(string PlanSha256, int CoverageOrdinal,
    GoogleGenerationSubmissionEnvelope Envelope, long EstimateMinorUnits);
