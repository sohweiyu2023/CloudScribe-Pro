namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleGenerationParsedResponse(ReadOnlyMemory<byte> AudioBytes, string? ProviderOperationId);
