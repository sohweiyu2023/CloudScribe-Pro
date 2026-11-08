using System.Text;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.Application.Documents;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleFullDocumentCostTests
{
    [Theory]
    [InlineData("plain-ascii")]
    [InlineData("unicode-and-escaping")]
    public void Neural2FullDocumentUsesAuthenticatedRateForEveryExactRequest(string kind)
    {
        string text = string.Equals(kind, "plain-ascii", StringComparison.Ordinal) ? new string('a', 8_166)
            : string.Concat(Enumerable.Repeat("Read \"this\" \\ caf\u00e9 \u4e2d\u6587 e\u0301 \U0001f600.\r\n", 200));
        var document = new DocumentSnapshot(Guid.NewGuid(), "Synthetic", text, 0, 0, DocumentStatus.Active,
            false, Guid.NewGuid(), null, null, 0);
        var controls = new V222ControlSet(new StrictJsonObjectReader());
        var rate = GoogleVoiceComparisonInformation.Rate(controls, "en-US-Neural2-D")!;
        Assert.Equal(16m, rate.UsdPerMillion);
        var plan = ChirpArticlePlanCompiler.Compile(document, "en-US", "en-US-Neural2-D");
        long expected = 0, actual = 0, bytes = 0;
        foreach (var part in plan.Parts)
        {
            var wire = ChirpArticlePlanCompiler.CompilePart(text, plan, part);
            var cost = GoogleTtsStudioCostEstimator.Estimate(controls, plan.VoiceName, wire.Payload, "global");
            using var payload = JsonDocument.Parse(wire.Payload);
            string spoken = payload.RootElement.GetProperty("input").GetProperty("text").GetString()!;
            long inputBytes = Encoding.UTF8.GetByteCount(spoken);
            Assert.Equal(rate.RateId, cost.RateId);
            Assert.Equal(inputBytes, cost.EstimatedBillableCharacters);
            Assert.Equal(inputBytes, cost.InputUtf8Bytes);
            Assert.InRange(wire.Payload.Length, 1, 5_000);
            expected += checked((long)decimal.Ceiling(inputBytes * 16m / 10_000m));
            actual += cost.MaximumMinorUnits; bytes += inputBytes;
        }
        Assert.Equal(Encoding.UTF8.GetByteCount(text), bytes);
        Assert.Equal(expected, actual);
        Assert.True(actual >= decimal.Ceiling(bytes * 16m / 10_000m));
        Assert.True(plan.Parts.Count > 1);
    }
}
