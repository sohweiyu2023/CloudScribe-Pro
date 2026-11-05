using System.Text.Json;
using System.Xml.Linq;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleSentenceDiagnosticCompilerTests
{
    private static GoogleSpeechCompilationOptions Options => new("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3", 5_000);

    [Fact]
    public void ExistingMarkersPreserveQuotesWhitespaceNumbersAndAbbreviations()
    {
        const string text = "\u7532\u3002\r\n\u201c\u4e59\uff1f\u201d\n3.14 e.g. example.com end?";
        var markers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(text);
        Assert.Equal(3, markers.EndScalars.Count);
        var wire = GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(text, Options, markers.EndScalars);
        using var json = JsonDocument.Parse(wire.Payload);
        var root = XElement.Parse(json.RootElement.GetProperty("input").GetProperty("ssml").GetString()!, LoadOptions.PreserveWhitespace);
        Assert.Equal(text, root.Value);
        Assert.Equal(3, root.Elements().Count());
        Assert.Contains("3.14 e.g. example.com end?", root.Elements().Last().Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A<&>\u3002\n\n\"B\u3002\"", new[] { 5, 6, 7, 11 })]
    [InlineData("\u7532\u3002\r\n\u4e59\u3002", new[] { 2, 4, 6 })]
    [InlineData("\U0001F600\u3002\u4e59\u3002", new[] { 2, 4 })]
    [InlineData("\u7532\t\u4e59!", new[] { 4 })]
    public void ExactTextEscapesRoundtripWithoutChangingVoiceOrAddingDirectives(string text, int[] ends)
    {
        var wire = GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(text, Options, ends);
        using var json = JsonDocument.Parse(wire.Payload);
        Assert.False(json.RootElement.GetProperty("input").TryGetProperty("text", out _));
        var root = XElement.Parse(json.RootElement.GetProperty("input").GetProperty("ssml").GetString()!, LoadOptions.PreserveWhitespace);
        Assert.Equal(text, root.Value);
        Assert.All(root.Elements(), e => { Assert.Equal("s", e.Name.LocalName); Assert.False(e.HasAttributes); Assert.False(e.HasElements); });
        Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", json.RootElement.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal("MP3", json.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Empty(wire.Degradations);
        Assert.Equal(wire.PayloadSha256, GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(text, Options, ends).PayloadSha256);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 4 })]
    [InlineData(new[] { 2, 2, 4 })]
    [InlineData(new[] { 2, 3 })]
    [InlineData(new[] { 2, 5 })]
    public void RejectsHardCutsDuplicatesGapsAndOverflow(int[] ends) =>
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic("\u7532\u3002\u4e59\u3002", Options, ends));

    [Fact]
    public void RejectsOversizeInputAndSentenceMarkersInsideTextElements()
    {
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(new string('x', 2_001), Options, [2_001]));
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(".\u0301x", Options, [1, 3]));
    }

    [Fact]
    public void PlainTextIdentityCannotAuthorizeTheChangedWireShape()
    {
        const string text = "\u7532\u3002\u4e59\u3002";
        var plain = GoogleSpeechPlanCompiler.Compile(new SpeechPlan("cmn-CN", [new SpeechText(text)], "fixture"), Options);
        var marked = GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(text, Options, [2, 4]);
        Assert.NotEqual(plain.PayloadSha256, marked.PayloadSha256, StringComparer.Ordinal);
        using var json = JsonDocument.Parse(plain.Payload);
        Assert.Equal(text, json.RootElement.GetProperty("input").GetProperty("text").GetString());
    }

    [Fact]
    public void RejectsUnreviewedFormatInvalidXmlAndCompiledByteOverflow()
    {
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic("ok", Options with { VoiceName = "cmn-CN-Wavenet-B" }, [2]));
        Assert.ThrowsAny<ArgumentException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic("a\0b", Options, [3]));
        Assert.Throws<InvalidOperationException>(() => GoogleSpeechPlanCompiler.CompileSentenceMarkedDiagnostic(new string('x', 500), Options with { MaximumPayloadBytes = 256 }, [500]));
    }
}
