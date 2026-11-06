namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleLongAudioRecoveryProvenance(string RootBindingSha256, string ParentBindingSha256,
    int StartScalar, int EndScalarExclusive, string PlannerVersion, string ParentCheckpointSha256);
