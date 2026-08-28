namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleHttpTransportRequest(
    Uri Endpoint,
    string CredentialReferenceId,
    ReadOnlyMemory<byte> Payload,
    int MaximumResponseBytes = 16 * 1024 * 1024);
