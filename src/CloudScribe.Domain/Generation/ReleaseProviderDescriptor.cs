namespace CloudScribe.Domain.Generation;

public sealed record ReleaseProviderDescriptor(
    string ProviderStableId,
    string DisplayName,
    string ControlMemberSha256,
    IReadOnlySet<string> OperationStableIds);
