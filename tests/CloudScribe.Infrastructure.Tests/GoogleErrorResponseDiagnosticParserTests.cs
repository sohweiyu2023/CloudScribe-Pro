using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleErrorResponseDiagnosticParserTests
{
    [Theory]
    [InlineData("Request contains an invalid argument.", GoogleValidationMessageHint.GenericValidation)]
    [InlineData("This request contains sentences that are too long.", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("This request contains sentences that are too long. Consider splitting up long sentences. PRIVATE_ECHO", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("Not: This request contains sentences that are too long. Consider splitting up long sentences.", GoogleValidationMessageHint.Unrecognized)]
    [InlineData("This request contains sentences that are too long. Consider splitting is not the reported issue.", GoogleValidationMessageHint.Unrecognized)]
    [InlineData("This request contains sentences that are too long. Consider splitting up long sentences with sentence-ending punctuation. PRIVATE_ECHO", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("This request contains sentences that are too long. Consider splitting up long sentences or adding punctuation.", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("This request contains sentences that are too long. Read more at https://example.invalid/PRIVATE_TOKEN", GoogleValidationMessageHint.SentenceLengthReported)]
    [InlineData("PRIVATE_ARTICLE_TOKEN_PATH", GoogleValidationMessageHint.Unrecognized)]
    [InlineData("Not: This request contains sentences that are too long.", GoogleValidationMessageHint.Unrecognized)]
    [InlineData("\"This request contains sentences that are too long.\"", GoogleValidationMessageHint.Unrecognized)]
    [InlineData("This request contains sentences that are too long. But that is not the problem.", GoogleValidationMessageHint.Unrecognized)]
    public void MissingDetailsAndMessageHintRemainDistinctFromCanonicalReason(string message, GoogleValidationMessageHint expected)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { error = new { status = "INVALID_ARGUMENT", message } });
        var result = GoogleErrorResponseDiagnosticParser.Parse(body);
        Assert.Equal(GoogleErrorBodyParseState.ValidError, result.Parse);
        Assert.Equal(GoogleErrorMemberState.TextValue, result.Message);
        Assert.Equal(message.Length, result.MessageUtf16Characters);
        Assert.Equal(GoogleErrorDetailsState.Absent, result.Details);
        Assert.Equal(expected, result.MessageHint);
        Assert.Equal(GoogleErrorDetailCategories.Missing, GoogleErrorDetailParser.Parse(body));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", GoogleErrorBodyParseState.MissingError)]
    [InlineData("[]", GoogleErrorBodyParseState.InvalidErrorShape)]
    [InlineData("{\"error\":[]}", GoogleErrorBodyParseState.InvalidErrorShape)]
    [InlineData("", GoogleErrorBodyParseState.Empty)]
    [InlineData("{", GoogleErrorBodyParseState.InvalidJson)]
    [InlineData("{\"error\":{},\"error\":{}}", GoogleErrorBodyParseState.Ambiguous)]
    [InlineData("{\"error\":{\"message\":\"a\",\"message\":\"b\"}}", GoogleErrorBodyParseState.Ambiguous)]
    [InlineData("{\"error\":{\"status\":\"a\",\"status\":\"b\"}}", GoogleErrorBodyParseState.Ambiguous)]
    [InlineData("{\"error\":{\"details\":[],\"details\":[]}}", GoogleErrorBodyParseState.Ambiguous)]
    [InlineData("{\"error\":{\"message\":\"\\uD800\"}}", GoogleErrorBodyParseState.InvalidJson)]
    [InlineData("{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"\\uD800\"}]}}", GoogleErrorBodyParseState.InvalidJson)]
    [InlineData("{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\",\"fieldViolations\":[{\"field\":\"\\uD800\"}]}]}}", GoogleErrorBodyParseState.InvalidJson)]
    public void AmbiguousMalformedOrAbsentErrorIsObservableWithoutText(string body, GoogleErrorBodyParseState expected) =>
        Assert.Equal(expected, GoogleErrorResponseDiagnosticParser.Parse(Encoding.UTF8.GetBytes(body)).Parse);

    [Theory]
    [InlineData("[]", GoogleErrorDetailsState.Empty)]
    [InlineData("[{\"@type\":\"PRIVATE_UNKNOWN\"}]", GoogleErrorDetailsState.UnrecognizedTypesOnly)]
    [InlineData("[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\"}]", GoogleErrorDetailsState.RecognizedTypes)]
    [InlineData("[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\"},{\"@type\":\"PRIVATE_UNKNOWN\"}]", GoogleErrorDetailsState.MixedTypes)]
    [InlineData("[1]", GoogleErrorDetailsState.Invalid)]
    [InlineData("{}", GoogleErrorDetailsState.Invalid)]
    [InlineData("[{\"@type\":null}]", GoogleErrorDetailsState.Invalid)]
    [InlineData("[{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\"},{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\"}]", GoogleErrorDetailsState.Ambiguous)]
    public void DetailsPresenceAndKnownTypeCountsDoNotCollapseToMissing(string details, GoogleErrorDetailsState expected)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"error\":{\"details\":" + details + "}}");
        var result = GoogleErrorResponseDiagnosticParser.Parse(body);
        Assert.Equal(expected, result.Details);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidMessageWrongStatusAndOversizedMessageDoNotProduceAHint()
    {
        var invalid = GoogleErrorResponseDiagnosticParser.Parse("{\"error\":{\"message\":5}}"u8.ToArray());
        Assert.Equal(GoogleErrorMemberState.Invalid, invalid.Message);
        Assert.Equal(GoogleValidationMessageHint.NotExamined, invalid.MessageHint);
        var wrongStatus = GoogleErrorResponseDiagnosticParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        { error = new { status = "INTERNAL", message = "Request contains an invalid argument." } }));
        Assert.Equal(GoogleValidationMessageHint.NotExamined, wrongStatus.MessageHint);
        var longMessage = GoogleErrorResponseDiagnosticParser.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        { error = new { status = "INVALID_ARGUMENT", message = new string('x', 2049) } }));
        Assert.Equal(GoogleValidationMessageHint.OverHintBound, longMessage.MessageHint);
    }

    [Fact]
    public void BoundsUtf8DepthAndDetailCount()
    {
        Assert.Equal(GoogleErrorBodyParseState.OverDiagnosticBound, GoogleErrorResponseDiagnosticParser.Parse(new byte[16385]).Parse);
        byte[] boundary = Encoding.UTF8.GetBytes("{\"error\":{},\"padding\":\"" + new string('x', 16359) + "\"}");
        Assert.Equal(16384, boundary.Length);
        Assert.Equal(GoogleErrorBodyParseState.ValidError, GoogleErrorResponseDiagnosticParser.Parse(boundary).Parse);
        Assert.Equal(GoogleErrorBodyParseState.InvalidJson, GoogleErrorResponseDiagnosticParser.Parse(new byte[] { 0xff }).Parse);
        string deep = "{\"error\":{},\"x\":" + new string('[', 9) + "0" + new string(']', 9) + "}";
        Assert.Equal(GoogleErrorBodyParseState.InvalidJson, GoogleErrorResponseDiagnosticParser.Parse(Encoding.UTF8.GetBytes(deep)).Parse);
        byte[] many = JsonSerializer.SerializeToUtf8Bytes(new { error = new { details = Enumerable.Repeat(new { x = 0 }, 17).ToArray() } });
        var result = GoogleErrorResponseDiagnosticParser.Parse(many);
        Assert.Equal(GoogleErrorDetailsState.Invalid, result.Details);
        Assert.Equal(17, result.DetailCount);
    }

    [Theory]
    [InlineData("{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":5}")]
    [InlineData("{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"metadata\":{\"PRIVATE\":5}}")]
    [InlineData("{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\",\"fieldViolations\":{}}")]
    [InlineData("{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\",\"fieldViolations\":[{\"field\":5}]}")]
    public void MalformedRecognizedDetailSuppressesOtherwiseKnownMessageHint(string detail)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"Request contains an invalid argument.\",\"details\":[" + detail + "]}}");
        var result = GoogleErrorResponseDiagnosticParser.Parse(body);
        Assert.Equal(GoogleErrorDetailsState.Invalid, result.Details);
        Assert.Equal(GoogleValidationMessageHint.NotExamined, result.MessageHint);
    }
}
