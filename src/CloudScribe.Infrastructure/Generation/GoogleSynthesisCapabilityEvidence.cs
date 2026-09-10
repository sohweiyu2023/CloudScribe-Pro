namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleSynthesisCapabilityEvidence(
    DateTimeOffset CapturedAtUtc,
    string ProvenanceId);