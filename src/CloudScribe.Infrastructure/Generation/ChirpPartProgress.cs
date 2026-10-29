namespace CloudScribe.Infrastructure.Generation;

public sealed record ChirpPartProgress(int CoverageOrdinal, int Scalars, string Status, bool Reused, bool CurrentlyAdmitted);
