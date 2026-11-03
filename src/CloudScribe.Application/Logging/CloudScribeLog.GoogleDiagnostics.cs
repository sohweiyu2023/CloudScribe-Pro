using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Logging;

public static partial class CloudScribeLog
{
    [LoggerMessage(EventId = 1605, Level = LogLevel.Information, Message = "Google dispatch content {Correlation}: SHA256 {PayloadSha256}, UTF8 bytes {PayloadBytes}, endpoint {Endpoint}, origin SHA256 {OriginSha256}, query present {HasQuery}. Exact owned content snapshot; no URI, query, credentials or request contents are retained.")]
    public static partial void GoogleSynthesisWireContent(ILogger logger, string correlation, string payloadSha256, int payloadBytes,
        GoogleSynthesisEndpointIdentity endpoint, string originSha256, bool hasQuery);

    [LoggerMessage(EventId = 1606, Level = LogLevel.Information, Message = "Google response metadata {Correlation}: HTTP {HttpStatus}, bytes consumed {BodyBytes}, read {ReadState}, media type {MediaType}, declared length {LengthState}. No header or body values retained.")]
    public static partial void GoogleSynthesisResponseMetadata(ILogger logger, string correlation, int httpStatus, int bodyBytes,
        GoogleResponseReadState readState, GoogleResponseMediaType mediaType, GoogleResponseLengthState lengthState);

    [LoggerMessage(EventId = 1607, Level = LogLevel.Information, Message = "Google error parse {Correlation}: HTTP {HttpStatus}, parse {ParseState}, status member {StatusMember}, message member {MessageMember}, message UTF16 characters {MessageCharacters}, details {DetailsState}, bounded detail count {DetailCount}, local informational hint {MessageHint}. Hint is not canonical cause, retry permission or billing settlement. No provider text retained.")]
    public static partial void GoogleSynthesisErrorParse(ILogger logger, string correlation, int httpStatus, GoogleErrorBodyParseState parseState,
        GoogleErrorMemberState statusMember, GoogleErrorMemberState messageMember, int messageCharacters,
        GoogleErrorDetailsState detailsState, int detailCount, GoogleValidationMessageHint messageHint);

    [LoggerMessage(EventId = 1608, Level = LogLevel.Information, Message = "Google explanation evidence {Correlation}: HTTP {HttpStatus}, response bytes {BodyBytes}, private retention {Retention}. Routine logs omit provider wording. NoActivePrivateTicket means off, expired, or not bound; it is not proof that no explanation was received. Pending is not durable success.")]
    public static partial void GoogleExplanationEvidence(ILogger logger, string correlation, int httpStatus,
        int bodyBytes, GoogleExplanationRetentionState retention);

    [LoggerMessage(EventId = 1609, Level = LogLevel.Information, Message = "Private explanation persistence {Correlation}: payload SHA256 {PayloadSha256}, retention {Retention}, redacted UTF8 bytes {RedactedBytes}, truncated {Truncated}, other provider details omitted {DetailsOmitted}. No private message or path retained in routine logs.")]
    public static partial void GooglePrivateExplanationPersistence(ILogger logger, string correlation, string payloadSha256,
        GoogleExplanationRetentionState retention, int redactedBytes, bool truncated, bool? detailsOmitted);

    [LoggerMessage(EventId = 1610, Level = LogLevel.Information, Message = "Google request-ID evidence {Correlation}: header kind {HeaderKind}, state {RequestIdState}, identifier SHA256 {RequestIdSha256}. Header values are not retained; this hash is not a provider lookup ID or billing outcome.")]
    public static partial void GoogleRequestIdEvidence(ILogger logger, string correlation, GoogleProviderRequestIdHeader headerKind,
        GoogleProviderRequestIdState requestIdState, string? requestIdSha256);

    [LoggerMessage(EventId = 1611, Level = LogLevel.Information, Message = "Google transport ended {Correlation}: last stage {LastStage}, HTTP {HttpStatus}, elapsed milliseconds {ElapsedMilliseconds}. No automatic retry is authorized.")]
    public static partial void GoogleTransportEnded(ILogger logger, string correlation, GoogleSynthesisStage lastStage,
        int httpStatus, long elapsedMilliseconds);

    [LoggerMessage(EventId = 1612, Level = LogLevel.Warning, Message = "Completion guard rejected {Correlation}: operation {Operation}, reason {ReasonCode}, code {ProtocolCode}, category {Category}, component {FailureComponent}, source line {FailureSourceLine}, type {FailureType}, elapsed milliseconds {ElapsedMilliseconds}. No subprocess prose or request contents retained; no automatic retry.")]
    public static partial void CompletionGuardRejected(ILogger logger, Guid correlation, CompletionProtocolOperation operation,
        int reasonCode, CompletionProtocolCode protocolCode, LocalRequestReason category, CompletionProtocolFailureComponent failureComponent,
        int failureSourceLine, CompletionProtocolExceptionType failureType, long elapsedMilliseconds);
}
