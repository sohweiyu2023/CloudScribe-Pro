using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleErrorDetailParserTests
{
    [Fact]
    public void ParsesOnlyCanonicalAllowlistedReasonDomainAndField()
    {
        const string marker = "PRIVATE_TEXT_TOKEN_PATH";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = new
            {
                status = "INVALID_ARGUMENT",
                message = marker,
                details = new object[]
                {
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    { ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo", ["reason"] = "SERVICE_DISABLED", ["domain"] = "googleapis.com", ["metadata"] = new { secret = marker } },
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    { ["@type"] = "type.googleapis.com/google.rpc.BadRequest", ["fieldViolations"] = new[] { new { field = "input.text", description = marker } } },
                },
            },
        });
        var categories = GoogleErrorDetailParser.Parse(body);
        Assert.Equal(new(GoogleErrorReasonCategory.ServiceDisabled, GoogleErrorDomainCategory.GoogleApis, GoogleErrorFieldCategory.InputText), categories);
        Assert.DoesNotContain(marker, categories.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"sentences too long PRIVATE\"}}")]
    public void MissingStructuredDetailsNeverInferReasonFromMessage(string body) =>
        Assert.Equal(GoogleErrorDetailCategories.Missing, GoogleErrorDetailParser.Parse(Encoding.UTF8.GetBytes(body)));

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"error\":{},\"error\":{}}")]
    [InlineData("{\"error\":{\"details\":{},\"details\":[]}}")]
    [InlineData("{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"SERVICE_DISABLED\",\"reason\":\"PRIVATE\"}]}}")]
    [InlineData("{\"error\":{\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\"},{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\"}]}}")]
    public void MalformedOrAmbiguousEvidenceFailsToUnknown(string body) =>
        Assert.Equal(GoogleErrorDetailCategories.Unknown, GoogleErrorDetailParser.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void UnknownFreeFormValuesNeverEscapeTheEnumsAndBodyIsBounded()
    {
        var categories = GoogleErrorDetailParser.Parse(Encoding.UTF8.GetBytes("""
            {"error":{"details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"PRIVATE_REASON","domain":"PRIVATE_DOMAIN"},{"@type":"type.googleapis.com/google.rpc.BadRequest","fieldViolations":[{"field":"input.text.PRIVATE_PATH","description":"PRIVATE_DESCRIPTION"}]}]}}
            """));
        Assert.Equal(GoogleErrorDetailCategories.Unknown, categories);
        Assert.DoesNotContain("PRIVATE", categories.ToString(), StringComparison.Ordinal);
        Assert.Equal(GoogleErrorDetailCategories.Unknown, GoogleErrorDetailParser.Parse(new byte[16385]));
    }
}
