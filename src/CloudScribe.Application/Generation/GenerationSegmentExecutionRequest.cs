using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public sealed record GenerationSegmentExecutionRequest(
    string ProviderStableId,
    string OperationStableId,
    string AccountId,
    string VoiceStableId,
    string CompilationProfileId,
    string IdempotencyKey,
    ReadOnlyMemory<byte> CompiledPayload,
    string OutputFormat,
    GenerationCacheTrustContext? CacheTrustContext = null,
    bool ForceFresh = false,
    CacheReuseMediaMetadata? ExpectedCacheMediaMetadata = null);
