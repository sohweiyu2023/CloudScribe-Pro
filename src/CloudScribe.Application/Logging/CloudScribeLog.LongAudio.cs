using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1620, Level = LogLevel.Information, Message = "Long Audio recovery v1: root {RootAttemptId}, attempt {AttemptId}, parent {ParentAttemptId}, source scalar range {StartScalar}..{EndScalarExclusive}, depth {SplitDepth}, payload SHA256 {PayloadSha256}, stage {Stage}, safe failure kind {FailureKind}. InspectOrSubmit is not evidence of a new POST; join exact exchange and private retention by attempt. No text, credentials or output paths retained.")]
    public static partial void LongAudioRecovery(ILogger logger, Guid rootAttemptId, Guid attemptId, Guid? parentAttemptId,
        int startScalar, int endScalarExclusive, int splitDepth, string payloadSha256, LongAudioRecoveryStage stage, int? failureKind);

    [LoggerMessage(EventId = 1619, Level = LogLevel.Information, Message = "Long Audio exchange v1: attempt {AttemptId}, correlation {Correlation}, endpoint {Endpoint}, POST method {IsPost}, send started {SendStarted}, caller cancelled {CallerCancelled}, payload SHA256 {PayloadSha256}, wire bytes {PayloadBytes}, HTTP {HttpStatus}, body read {ReadState}, observed bytes {ObservedBytes}, duration milliseconds {DurationMilliseconds}, private record {PrivateRecordId}, numeric failure {LocalHResult}. A started send does not prove receipt or billing. Null read means no response body was observed; no URI, headers, input or error prose retained.")]
    public static partial void LongAudioExchange(ILogger logger, Guid attemptId, string correlation, LongAudioEndpoint endpoint, bool isPost, bool sendStarted, bool callerCancelled,
        string payloadSha256, int payloadBytes, int httpStatus, GoogleResponseReadState? readState, int observedBytes,
        long durationMilliseconds, Guid? privateRecordId, int? localHResult);

    [LoggerMessage(EventId = 1618, Level = LogLevel.Information, Message = "Long Audio workflow v1: attempt {AttemptId}, document {DocumentId}, revision {RevisionId}, payload SHA256 {PayloadSha256}, stage {Stage}, operation state {OperationState}, HTTP {HttpStatus}, RPC {RpcCode}, elapsed milliseconds {DurationMilliseconds}, private record {PrivateRecordId}, readback {ReadbackVerified}, truncated {Truncated}, omitted {DetailsOmitted}, provider body bytes {ObservedBytes}, numeric local error {LocalHResult}. Operation completion is not media verification or billing settlement; no source, credentials, bucket or operation name retained.")]
    public static partial void LongAudioWorkflow(ILogger logger, Guid attemptId, Guid documentId, Guid revisionId,
        string payloadSha256, LongAudioWorkflowStage stage, int? operationState, int? httpStatus, int? rpcCode,
        long durationMilliseconds, Guid? privateRecordId, bool? readbackVerified, bool? truncated, bool? detailsOmitted,
        int? observedBytes, int? localHResult);
}
