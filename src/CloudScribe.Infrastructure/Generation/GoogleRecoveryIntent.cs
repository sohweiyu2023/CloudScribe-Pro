namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact offline preparation inputs. Neither a preview nor diagnostics authorize dispatch.</summary>
public sealed record GoogleRecoveryIntent(Guid DocumentId, Guid RevisionId, string ProjectId,
    string PreviousIdempotencyKey, GoogleGenerationSubmissionEnvelope Envelope,
    string Currency, int Scale, long EstimateMinorUnits, ReadOnlyMemory<byte> CompiledPayload);
