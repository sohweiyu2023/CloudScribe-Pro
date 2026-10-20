namespace CloudScribe.Infrastructure.Generation;

public sealed record LocalRequestStatus(int Total, int Unresolved, IReadOnlyList<string> Rows);
