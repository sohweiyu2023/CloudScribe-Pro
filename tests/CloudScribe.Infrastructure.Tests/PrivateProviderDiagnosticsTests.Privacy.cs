using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Theory]
    [InlineData("A useful unfamiliar boundary validation explanation.")]
    [InlineData("Input contains a provider-specific validation condition at offset 17.")]
    public void UnknownUsefulWordingIsPreservedWithoutClassifierMatch(string message) =>
        Assert.Equal(message, Redact(message));

    [Theory]
    [InlineData("SYNTHETIC_SECRET_ACCESS_TOKEN")]
    [InlineData("Bearer ANOTHER_SYNTHETIC_TOKEN")]
    [InlineData("api_key=DIFFERENT_SYNTHETIC_KEY")]
    [InlineData("\"access_token\":\"DIFFERENT_SYNTHETIC_TOKEN\"")]
    [InlineData("-----BEGIN PRIVATE KEY-----SYNTHETIC_KEY-----END PRIVATE KEY-----")]
    [InlineData("SYNTHETIC PRIVATE INPUT DOCUMENT WITH A LONG SECRET FRAGMENT")]
    [InlineData("PRIVATE INPUT DOCUMENT WITH A LONG SECRET")]
    public void KnownCredentialAndInputEchoesDoNotSurvive(string echo)
    {
        string result = Redact("Unfamiliar provider failure: " + echo)!;
        Assert.StartsWith("Unfamiliar provider failure:", result, StringComparison.Ordinal);
        Assert.DoesNotContain(echo, result, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET", result, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE INPUT", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodedKnownEchoesAndFragmentsAreRedactedBeforeTruncation()
    {
        const string input = "SYNTHETIC PRIVATE INPUT DOCUMENT WITH A LONG SECRET FRAGMENT";
        foreach (var echo in new[] { Uri.EscapeDataString(input), Convert.ToBase64String(Encoding.UTF8.GetBytes(input)), JsonSerializer.Serialize(input)[1..^1] })
            Assert.DoesNotContain(echo, Redact("Unknown validation: " + echo)!, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlAndControlsAreInertOrRemoved()
    {
        string result = Redact("<script>doBadThing()</script><a href='https://example.test'>link</a>\u001b[0m\u202e\0 Useful failure")!;
        Assert.DoesNotContain('<', result); Assert.DoesNotContain('>', result);
        Assert.DoesNotContain('\u001b', result); Assert.DoesNotContain('\u202e', result); Assert.DoesNotContain('\0', result);
        Assert.Contains("Useful failure", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"\"}}")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":null}}")]
    [InlineData("{\"error\":{\"status\":\"INVALID_ARGUMENT\",\"message\":\"one\",\"message\":\"two\"}}")]
    [InlineData("{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"private\"}}")]
    [InlineData("{\"error\":{\"status\":17,\"message\":\"private\"}}")]
    public void InvalidEmptyMissingOrOtherErrorBodiesAreNotCaptured(string body) =>
        Assert.Null(PrivateProviderMessageRedactor.Extract(Encoding.UTF8.GetBytes(body), Input(), "SYNTHETIC_SECRET_ACCESS_TOKEN"));

    [Fact]
    public void OversizedAndInvalidUtf8AreWithheld()
    {
        Assert.Null(PrivateProviderMessageRedactor.Extract(new byte[16385], Input(), "token"));
        Assert.Null(PrivateProviderMessageRedactor.Extract(new byte[] { 0xff, 0xfe }, Input(), "token"));
    }

    [Fact]
    public async Task WholeEscapedRecordAndUnicodeMessageAreBounded()
    {
        using var f = new Fixture(); f.Arm(); await f.SendAsync(string.Concat(Enumerable.Repeat("中😀", 600)));
        var record = Assert.Single(f.Store.ReadRecords());
        Assert.True(record.Truncated); Assert.True(Encoding.UTF8.GetByteCount(record.Message) <= 2048);
        Assert.All(Directory.GetFiles(f.Root), file => Assert.True(new FileInfo(file).Length <= 4096));
        Assert.DoesNotContain('\ufffd', record.Message);
    }

    [Fact]
    public void InterruptedRedactedTemporaryRecordIsCleanedWithoutReadingIt()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        string temp = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllText(temp, "REDACTED synthetic partial record");
        Assert.Empty(f.Store.ReadRecords()); Assert.False(File.Exists(temp));
    }

    private static byte[] Input() => JsonSerializer.SerializeToUtf8Bytes(new { input = new { text = "SYNTHETIC PRIVATE INPUT DOCUMENT WITH A LONG SECRET FRAGMENT" } });
    private static string? Redact(string message) => PrivateProviderMessageRedactor.Extract(
        JsonSerializer.SerializeToUtf8Bytes(new { error = new { status = "INVALID_ARGUMENT", message } }), Input(), "SYNTHETIC_SECRET_ACCESS_TOKEN");
}
