using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleFullDocumentPlanCompilerTests
{
    private const string Neural2 = "en-US-Neural2-D";

    [Theory]
    [InlineData(Neural2)]
    [InlineData("en-US-Chirp3-HD-Aoede")]
    public void SameLongDocumentCompilesLosslesslyWithExactSelectedVoiceAndByteBound(string voice)
    {
        var document = Source(ComparisonText());
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", voice);
        Assert.Equal(8_166, plan.SourceScalars);
        Assert.True(plan.Parts.Count > 1);
        Assert.Equal(document.Text, string.Concat(plan.Parts.Select(part => ReadText(document, plan, part))));
        ChirpArticlePlanCompiler.RequireCurrent(plan, document);
        ChirpArticlePlanCompiler.RequireIntegrity(plan);
        if (string.Equals(voice, Neural2, StringComparison.Ordinal))
        {
            Assert.Equal(ChirpArticlePlanCompiler.Neural2Version, plan.PlannerVersion);
            Assert.InRange(plan.Parts.Count, 2, 3);
            Assert.Contains(plan.Parts, part => part.Range.EndScalarExclusive - part.Range.StartScalar > 500);
        }
        else Assert.Equal(ChirpNarrationPlanner.Version, plan.PlannerVersion);
    }

    [Fact]
    public void LegacyChirpWireAndPlanIdentityStayPinned()
    {
        var document = Source("A plain, unchanged article.");
        var plan = ChirpArticlePlanCompiler.Compile(document, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var wire = ChirpArticlePlanCompiler.CompilePart(document.Text, plan, Assert.Single(plan.Parts));
        Assert.Equal("4d6bacf285dbe211d11d0d3f6936bb208a729106bfc7ba30bf477c41713c0881", wire.PayloadSha256);
        Assert.Equal("2cb7212c7dab68eb59e04610592f83215e9cc16230fea24df2d6ab80a71d1645", plan.PlanSha256);
        Assert.Equal("""{"input":{"text":"A plain, unchanged article."},"voice":{"languageCode":"cmn-CN","name":"cmn-CN-Chirp3-HD-Alnilam"},"audioConfig":{"audioEncoding":"MP3"}}""",
            Encoding.UTF8.GetString(wire.Payload.Span));
    }

    [Fact]
    public void Neural2RetainsItsExistingDefaultControlEnvelope()
    {
        var document = Source("Read exactly this.");
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2);
        var wire = ChirpArticlePlanCompiler.CompilePart(document.Text, plan, Assert.Single(plan.Parts));
        Assert.Equal("""{"input":{"text":"Read exactly this."},"voice":{"languageCode":"en-US","name":"en-US-Neural2-D"},"audioConfig":{"audioEncoding":"MP3","speakingRate":1,"pitch":0,"volumeGainDb":0}}""",
            Encoding.UTF8.GetString(wire.Payload.Span));
    }

    [Fact]
    public void EscapingUnicodeGraphemesAndCrLfArePreservedWithinCompiledBytes()
    {
        var document = Source(string.Concat(Enumerable.Repeat("\"\\<&> café e\u0301 👩‍👩‍👧‍👦 中文。\r\n", 400)));
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2);
        string[] texts = plan.Parts.Select(part => ReadText(document, plan, part)).ToArray();
        Assert.Equal(document.Text, string.Concat(texts));
        var boundaries = StringInfo.ParseCombiningCharacters(document.Text).Append(document.Text.Length).ToHashSet();
        int utf16 = 0;
        foreach (string text in texts)
        {
            Assert.Contains(utf16, boundaries);
            utf16 += text.Length;
            Assert.Contains(utf16, boundaries);
        }
        ChirpArticlePlanCompiler.RequireCurrent(plan, document);
    }

    [Fact]
    public void LongUnbrokenTokenUsesWholeTextElementsWithoutAnArbitraryCharacterWindow()
    {
        var document = Source(new string('a', 8_166));
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2);
        Assert.Equal(2, plan.Parts.Count);
        Assert.Equal(5_000, plan.Parts[0].PayloadBytes);
        Assert.Equal(document.Text, string.Concat(plan.Parts.Select(part => ReadText(document, plan, part))));
    }

    [Theory]
    [InlineData("invalid-unicode")]
    [InlineData("oversized-grapheme")]
    [InlineData("whitespace-range")]
    [InlineData("source-bound")]
    public void PathologicalSourceFailsLocallyWithoutLossyReplacement(string kind)
    {
        string text = kind switch
        {
            "invalid-unicode" => "read \ud800 exactly",
            "oversized-grapheme" => "a" + new string('\u0301', 1_000),
            "whitespace-range" => new string(' ', 6_000) + "read this",
            _ => new string('a', ChirpNarrationPlanner.MaximumSourceScalars + 1),
        };
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(Source(text), "en-US", Neural2));
    }

    [Fact]
    public void OversizedEnvelopeAlsoFailsBeforeProducingARequest()
    {
        string language = new('a', 5_000);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(Source("text"), language, language + "-Neural2-D"));
    }

    [Fact]
    public void VerifiedReuseIsRetainedExactlyOnceWithoutPayloadAndEveryIdentityIsRevalidated()
    {
        var document = Source(ComparisonText());
        var reuse = new ChirpNarrationReuse(100, 300, ArticleAudioManifest.HashText(document.Text[100..300]), new string('a', 64));
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2, [reuse]);
        var accepted = Assert.Single(plan.Parts, part => part.Range.ReusedReceiptSha256 is not null);
        Assert.Null(accepted.PayloadSha256); Assert.Equal(0, accepted.PayloadBytes);
        Assert.Equal(document.Text, string.Concat(ChirpNarrationPlanner.SliceAll(document.Text, plan.Parts.Select(part => part.Range).ToArray())));
        ChirpArticlePlanCompiler.RequireCurrent(plan, document, [reuse]);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document, [reuse with { ReceiptSha256 = new string('b', 64) }]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan, document with { Text = document.Text + "edited" }, [reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { VoiceName = "en-US-Neural2-C" }, document, [reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { PlannerVersion = ChirpNarrationPlanner.Version }, document, [reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { Parts = plan.Parts.Reverse().ToArray() }, document, [reuse]));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2, [reuse, reuse]));
    }

    [Fact]
    public void ReuseCannotSplitSurrogatesCombiningCharactersOrCrLf()
    {
        var document = Source("a e\u0301\r\nmore text");
        foreach ((int start, int end) in new[] { (2, 3), (4, 5) })
        {
            var reuse = new ChirpNarrationReuse(start, end, ArticleAudioManifest.HashText(document.Text[start..end]), new string('a', 64));
            Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2, [reuse]));
        }
    }

    [Theory]
    [InlineData("en-US", Neural2, true)]
    [InlineData("en-US", "en-US-Chirp3-HD-Aoede", true)]
    [InlineData("en-GB", Neural2, false)]
    [InlineData("en-US", "en-US-Neural2-D-other", false)]
    [InlineData("en-US", "en-US-Neural2-", false)]
    [InlineData("en-US", "en-US-Standard-D", false)]
    [InlineData("en-US", "en-US-Wavenet-D", false)]
    [InlineData("", Neural2, false)]
    public void SupportedFamilyShapeNeverSubstitutesForAnotherVoice(string language, string voice, bool supported)
    {
        Assert.Equal(supported, GoogleFullDocumentVoiceSupport.Supports(language, voice));
        if (!supported && language.Length != 0)
            Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.Compile(Source("text"), language, voice));
    }

    [Fact]
    public void Neural2RejectsChirpSentenceGroupingRefinementAndSsmlRecovery()
    {
        var document = Source("One sentence, another clause; final words.");
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", Neural2);
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentences(document, "en-US", Neural2));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentences(document, "en-US", Neural2, inputFormat: new(1)));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileSentenceSuccessor(document, plan, []));
        var refinement = new ChirpFailedPlanRefinement(plan, 1, Guid.NewGuid(), new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompileRefined(document, refinement, []));
        Assert.Throws<InvalidOperationException>(() => ChirpFailedRangeSubdivisionCompiler.Compile(document, plan, 1));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.RequireCurrent(plan with { InputFormat = new(1) }, document));
        Assert.Throws<InvalidOperationException>(() => ChirpArticlePlanCompiler.CompilePart(document.Text, plan with { InputFormat = new(1) }, plan.Parts[0]));
        Guid root = Guid.NewGuid(); var part = plan.Parts[0];
        var evidence = new ChirpSentenceSplitEvidence(root, root, part.PayloadSha256!, part.PayloadBytes, new string('a', 64), new string('b', 64),
            400, GoogleErrorStatus.InvalidArgument, GoogleValidationMessageHint.SentenceLengthReported, true, true, false, false, false);
        var grant = new ChirpSentenceSplitGrant(1, Guid.NewGuid(), root, plan.SourceSha256, new string('c', 64), 2, 2, 2);
        Assert.Throws<InvalidOperationException>(() => ChirpBoundedSentenceSplit.Compile(document.Text, part.Range,
            new("en-US", Neural2, "MP3", 5_000), evidence, grant, new(0, 0, 0), _ => 1));
    }

    private static string ReadText(DocumentSnapshot document, ChirpArticlePlanProposal plan, ChirpArticlePlanPart part)
    {
        var compiled = ChirpArticlePlanCompiler.CompilePart(document.Text, plan, part);
        Assert.Equal(part.PayloadSha256, compiled.PayloadSha256); Assert.Equal(part.PayloadBytes, compiled.Payload.Length);
        Assert.InRange(compiled.Payload.Length, 1, 5_000);
        using var json = JsonDocument.Parse(compiled.Payload);
        Assert.Equal(plan.VoiceName, json.RootElement.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal(plan.LanguageCode, json.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal("MP3", json.RootElement.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        return json.RootElement.GetProperty("input").GetProperty("text").GetString()!;
    }

    internal static string ComparisonText() => string.Concat(Enumerable.Repeat("The river follows a quiet path through the valley, and every word remains unchanged. ", 110))[..8_166];
    private static DocumentSnapshot Source(string text) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Synthetic", text,
        0, 0, DocumentStatus.Active, false, Guid.Parse("22222222-2222-2222-2222-222222222222"), null, null, 0);
}
