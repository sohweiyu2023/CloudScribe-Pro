using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1617, Level = LogLevel.Information, Message = "Article part provenance v1: job {JobId}, internal ordinal {Ordinal}, authorization {AuthorizationId}, request key SHA256 {IdempotencyKeySha256}, payload SHA256 {PayloadSha256}, automatic split depth {SplitDepth}. Zero is this job's original request, not all historical planning depth. Identity only; no acceptance or permission implied.")]
    public static partial void ArticlePartProvenance(ILogger logger, Guid jobId, int ordinal, Guid authorizationId,
        string idempotencyKeySha256, string payloadSha256, int splitDepth);

    [LoggerMessage(EventId = 1616, Level = LogLevel.Information, Message = "Google input structure v1: correlation {Correlation}, payload SHA256 {PayloadSha256}, voice {VoiceName}, mode {InputMode}, input UTF8 bytes {InputUtf8Bytes}, source scalars {SourceScalars}, sentences {Sentences}, maximum sentence scalars {MaximumSentenceScalars}, maximum sentence UTF8 bytes {MaximumSentenceUtf8Bytes}, median scalars {MedianSentenceScalars}, p95 scalars {P95SentenceScalars}, scalar histogram <=32/33-64/65-128/129-256/>256 {Histogram}, explicit sentence markers {ExplicitSentenceMarkers}, observation complete {Complete}, split depth {SplitDepth} (-1 unavailable). Descriptive boundaries, not an acceptance threshold or causal conclusion. Join outcomes and private retention by correlation; no text retained.")]
    public static partial void GoogleInputStructure(ILogger logger, string correlation, string payloadSha256, string? voiceName,
        string inputMode, int inputUtf8Bytes, int sourceScalars, int sentences, int maximumSentenceScalars,
        int maximumSentenceUtf8Bytes, int medianSentenceScalars, int p95SentenceScalars, string histogram,
        bool explicitSentenceMarkers, bool complete, int splitDepth);
}
