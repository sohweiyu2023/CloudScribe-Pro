using System.Text;
using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class ChirpBoundedSentenceSplitTests
{
    [Fact]
    public void StructuralSourceBoundAllowsFifthSubdivisionWhileEveryChildStrictlyShrinks()
    {
        var f = Fixture(string.Join(", ", Enumerable.Range(0, 64).Select(x => "v" + x.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))) + ".");
        f = f with
        {
            Grant = f.Grant with
            {
                MaximumDepth = f.Source.EnumerateRunes().Count() - 1,
                MaximumChildAttempts = 320,
                MaximumAdditionalMinorUnits = 320
            }
        };
        for (int depth = 1; depth <= 5; depth++)
        {
            int previousLength = f.Range.EndScalarExclusive - f.Range.StartScalar;
            var result = Run(f); Assert.Equal(depth, result.ChildDepth); Assert.False(result.GrantsAuthority);
            Assert.All(result.Parts, p => Assert.True(p.Range.EndScalarExclusive - p.Range.StartScalar < previousLength));
            var child = result.Parts[0];
            f = f with
            {
                Range = child.Range,
                State = new(result.ChildDepth, result.ChildAttemptsAfterReservation, result.ReservedMinorUnitsAfterReservation),
                Evidence = f.Evidence with
                {
                    AuthorizationId = Guid.NewGuid(),
                    PayloadSha256 = child.Compilation.PayloadSha256,
                    PayloadBytes = child.Compilation.Payload.Length
                }
            };
        }
        Assert.Equal(5, f.State.Depth);
    }

    [Fact]
    public void ClosestNaturalCutCreatesTwoSourcePreservingSsmlRequestsOnly()
    {
        var f = Fixture("\u7532\u4e59\u4e19\uff0c \u4e01\u620a\uff1b\u5df1\u5e9a\u8f9b\uff0c\u58ec\u7678\u3002");
        var result = Run(f);
        Assert.False(result.GrantsAuthority);
        Assert.Equal(2, result.Parts.Count);
        Assert.Equal(8, result.Parts[0].Range.EndScalarExclusive);
        Assert.Equal(f.Source, string.Concat(result.Parts.Select(x => ChirpNarrationPlanner.Slice(f.Source, x.Range))));
        Assert.Equal(2, result.ChildAttemptsAfterReservation);
        Assert.Equal(2, result.ReservedMinorUnitsAfterReservation);
        foreach (var part in result.Parts)
        {
            using var json = JsonDocument.Parse(part.Compilation.Payload);
            Assert.False(json.RootElement.GetProperty("input").TryGetProperty("text", out _));
            Assert.Equal(ChirpNarrationPlanner.Slice(f.Source, part.Range),
                GoogleSpeechPlanCompiler.ReadSentenceMarkedText(json.RootElement.GetProperty("input").GetProperty("ssml").GetString()!));
            Assert.NotEqual(f.Evidence.PayloadSha256, part.Compilation.PayloadSha256, StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("pending")]
    [InlineData("output")]
    [InlineData("different-status")]
    [InlineData("generic-hint")]
    [InlineData("changed-wire")]
    public void TerminalEvidenceMustBeExactAndResolved(string mutation)
    {
        var f = Fixture("\u7532\uff0c\u4e59\u3002");
        var evidence = mutation switch
        {
            "uncertain" => f.Evidence with { Unresolved = true },
            "pending" => f.Evidence with { HasPendingWork = true },
            "output" => f.Evidence with { HasAcceptedOrPendingOutput = true },
            "different-status" => f.Evidence with { HttpStatus = 429 },
            "generic-hint" => f.Evidence with { MessageHint = GoogleValidationMessageHint.GenericValidation },
            _ => f.Evidence with { PayloadSha256 = new string('a', 64) }
        };
        Assert.Throws<InvalidOperationException>(() => Run(f with { Evidence = evidence }));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("attempts")]
    [InlineData("cost")]
    [InlineData("source")]
    [InlineData("approval")]
    public void PreAdmittedLimitsCannotBeResetOrInferredFromTheError(string mutation)
    {
        var f = Fixture("\u7532\uff0c\u4e59\u3002");
        f = mutation switch
        {
            "depth" => f with { State = f.State with { Depth = 2 } },
            "attempts" => f with { State = f.State with { ChildAttemptsUsed = 3 } },
            "cost" => f with { State = f.State with { ReservedMinorUnitsUsed = 3 } },
            "source" => f with { Grant = f.Grant with { SourceSha256 = new string('b', 64) } },
            _ => f with { Grant = f.Grant with { SourceApprovalSha256 = "" } }
        };
        Assert.Throws<InvalidOperationException>(() => Run(f));
    }

    [Fact]
    public void OneFailedChildCanReachSecondDepthWithoutResettingRootAllowance()
    {
        var f = Fixture("\u7532\u4e59\u4e19\uff0c \u4e01\u620a\uff1b\u5df1\u5e9a\u8f9b\uff0c\u58ec\u7678\u3002");
        var first = Run(f);
        var child = first.Parts[0];
        var second = Run(f with
        {
            Range = child.Range,
            Evidence = f.Evidence with
            {
                AuthorizationId = Guid.NewGuid(),
                PayloadSha256 = child.Compilation.PayloadSha256,
                PayloadBytes = child.Compilation.Payload.Length
            },
            State = new(1, first.ChildAttemptsAfterReservation, first.ReservedMinorUnitsAfterReservation)
        });
        Assert.Equal(2, second.ChildDepth);
        Assert.Equal(4, second.ChildAttemptsAfterReservation);
        Assert.Equal(4, second.ReservedMinorUnitsAfterReservation);
        Assert.Equal(ChirpNarrationPlanner.Slice(f.Source, child.Range),
            string.Concat(second.Parts.Select(x => ChirpNarrationPlanner.Slice(f.Source, x.Range))));
    }

    [Fact]
    public void NoPunctuationNeverFallsBackToHardCuts()
    {
        Assert.Throws<InvalidOperationException>(() => Run(Fixture("\u7532\u4e59\u4e19\u4e01\u620a")));
    }

    [Theory]
    [InlineData("\uff0c \u7532\u4e59\u4e19\u4e01")]
    [InlineData("\u7532\u4e59\u4e19\u4e01\uff0c ")]
    public void PunctuationOnlyChildrenAreNeverProposed(string source)
    {
        Assert.Throws<InvalidOperationException>(() => Run(Fixture(source)));
    }

    [Fact]
    public void AcceptedCoverageAndOverBudgetEstimateRejectWithoutAnyAuthority()
    {
        var f = Fixture("\u7532\uff0c\u4e59\u3002");
        Assert.Throws<InvalidOperationException>(() => Run(f with { Range = f.Range with { ReusedReceiptSha256 = new string('c', 64) } }));
        Assert.Throws<InvalidOperationException>(() => ChirpBoundedSentenceSplit.Compile(f.Source, f.Range, f.Options,
            f.Evidence, f.Grant, f.State, _ => 3));
    }

    [Fact]
    public void UnicodeTextElementsClosersAndWhitespaceRemainExact()
    {
        var f = Fixture("\U0001f469\u200d\U0001f4bb\u7532\uff0c\u201d \r\n\u4e59e\u0301\uff1b\U0001f1f8\U0001f1ec\u4e19\u3002");
        var result = Run(f);
        Assert.Equal(f.Source, string.Concat(result.Parts.Select(x => ChirpNarrationPlanner.Slice(f.Source, x.Range))));
        var textElements = System.Globalization.StringInfo.ParseCombiningCharacters(f.Source);
        int cutUtf16 = ChirpNarrationPlanner.Slice(f.Source, result.Parts[0].Range).Length;
        Assert.Contains(cutUtf16, textElements);
    }

    private static ChirpSentenceSplitProposal Run(SplitFixture f) => ChirpBoundedSentenceSplit.Compile(f.Source,
        f.Range, f.Options, f.Evidence, f.Grant, f.State, _ => 1);

    private static SplitFixture Fixture(string source)
    {
        var range = new ChirpNarrationRange(0, source.EnumerateRunes().Count(), ArticleAudioManifest.HashText(source), null);
        var options = new GoogleSpeechCompilationOptions("cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", "MP3", 5000);
        var wire = GoogleSpeechPlanCompiler.Compile(new("cmn-CN", [new SpeechText(source)], "synthetic"),
            options with { SentenceMarkers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(source) });
        var root = Guid.NewGuid();
        return new(source, range, options,
            new(root, root, wire.PayloadSha256, wire.Payload.Length, new string('d', 64), new string('e', 64),
                400, GoogleErrorStatus.InvalidArgument, GoogleValidationMessageHint.SentenceLengthReported, true, true, false, false, false),
            new(1, Guid.NewGuid(), root, ArticleAudioManifest.HashText(source), new string('f', 64), 2, 4, 4), new(0, 0, 0));
    }

    private sealed record SplitFixture(string Source, ChirpNarrationRange Range, GoogleSpeechCompilationOptions Options,
        ChirpSentenceSplitEvidence Evidence, ChirpSentenceSplitGrant Grant, ChirpSentenceSplitState State);
}
