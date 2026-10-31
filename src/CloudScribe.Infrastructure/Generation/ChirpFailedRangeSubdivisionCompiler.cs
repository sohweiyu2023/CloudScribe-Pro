using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static class ChirpFailedRangeSubdivisionCompiler
{
    public static ChirpFailedRangeSubdivision Compile(DocumentSnapshot document, ChirpArticlePlanProposal parent,
        int ordinal, IReadOnlyList<ChirpNarrationReuse>? verifiedReuse = null)
    {
        ChirpArticlePlanCompiler.RequireCurrent(parent, document, verifiedReuse);
        var part = parent.Parts.SingleOrDefault(x => x.CoverageOrdinal == ordinal)
            ?? throw new InvalidOperationException("The original immutable request is absent.");
        if (part.Range.ReusedReceiptSha256 is not null || part.PayloadSha256 is null)
            throw new InvalidOperationException("Accepted audio cannot become new synthesis requests.");
        var ranges = ChirpClauseSubdivision.Plan(document.Text, part.Range);
        List<ChirpArticlePlanPart> parts = [];
        foreach (var range in ranges)
        {
            string text = ChirpNarrationPlanner.Slice(document.Text, range);
            var payload = GoogleSpeechPlanCompiler.Compile(new SpeechPlan(parent.LanguageCode, [new SpeechText(text)], ChirpClauseSubdivision.Version),
                new(parent.LanguageCode, parent.VoiceName, "MP3", 5_000));
            parts.Add(new(parts.Count + 1, range, payload.PayloadSha256, payload.Payload.Length));
        }
        var proposal = new ChirpFailedRangeSubdivision(ChirpClauseSubdivision.Version, parent.PlanSha256, ordinal,
            part.Range, part.PayloadSha256, part.PayloadBytes, parent.LanguageCode, parent.VoiceName, string.Empty, parts.AsReadOnly());
        return proposal with { SubdivisionSha256 = ChirpPolicyValidation.Hash(proposal) };
    }

    public static void RequireCurrent(ChirpFailedRangeSubdivision proposal, DocumentSnapshot document,
        ChirpArticlePlanProposal parent, IReadOnlyList<ChirpNarrationReuse>? verifiedReuse = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var exact = Compile(document, parent, proposal.ParentCoverageOrdinal, verifiedReuse);
        if (!JsonSerializer.SerializeToUtf8Bytes(exact).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(proposal)))
            throw new InvalidOperationException("The exact source, voice, parent or subdivision changed.");
    }
}
