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
}
