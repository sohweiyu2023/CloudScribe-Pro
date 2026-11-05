using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpArticlePlanCompiler
{
    public const string SentenceMarkedArticleVersion = "chirp3-existing-sentence-markers-v1";
    /// <summary>Fresh article proposal only; an existing stored plan cannot be silently replaced by another root.</summary>
    public static ChirpArticlePlanProposal CompileSentences(DocumentSnapshot document, string language, string voice,
        IReadOnlyList<ChirpNarrationReuse>? verifiedReuse = null, ChirpArticleInputFormat? inputFormat = null)
    {
        var reuse = verifiedReuse ?? [];
        var original = Compile(document, language, voice, reuse);
        return CompileSentenceParts(document, original, reuse, null, inputFormat);
    }

    /// <summary>Offline proposal only; production verifies terminal predecessor work and every retained receipt.</summary>
    public static ChirpArticlePlanProposal CompileSentenceSuccessor(DocumentSnapshot document, ChirpArticlePlanProposal parent,
        IReadOnlyList<ChirpNarrationReuse> verifiedReuse, ChirpArticleInputFormat? inputFormat = null)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(verifiedReuse);
        if (parent.InputFormat is not null || parent.SentenceSuccessor is not null || parent.Refinement?.Parent.Refinement is not null
            || inputFormat is null && parent.Refinement?.SentenceMarkers is not null)
            throw new InvalidOperationException("Sentence successor depth exceeds its reviewed bound.");
        var original = parent.Parts.Where(x => x.Range.ReusedReceiptSha256 is not null)
            .Select(x => new ChirpNarrationReuse(x.Range.StartScalar, x.Range.EndScalarExclusive, x.Range.TextSha256, x.Range.ReusedReceiptSha256!)).ToArray();
        RequireCurrent(parent, document, original);
        if (verifiedReuse.Count > parent.Parts.Count || verifiedReuse.Select(x => (x.StartScalar, x.EndScalarExclusive)).Distinct().Count() != verifiedReuse.Count
            || original.Any(x => !verifiedReuse.Contains(x)))
            throw new InvalidOperationException("Every inherited accepted range must remain unchanged exactly once.");
        foreach (var reuse in verifiedReuse)
            if (parent.Parts.Count(x => x.Range.StartScalar == reuse.StartScalar && x.Range.EndScalarExclusive == reuse.EndScalarExclusive
                && string.Equals(x.Range.TextSha256, reuse.TextSha256, StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Sentence planning can reuse only exact accepted predecessor ranges.");
        return CompileSentenceParts(document, parent, verifiedReuse, new(parent), inputFormat);
    }

    private static ChirpArticlePlanProposal CompileSentenceParts(DocumentSnapshot document, ChirpArticlePlanProposal parent,
        IReadOnlyList<ChirpNarrationReuse> verifiedReuse, ChirpSentencePlanSuccessor? successor, ChirpArticleInputFormat? inputFormat)
    {
        if (inputFormat is not null && inputFormat.Version != 1) throw new InvalidOperationException("Unknown article input format.");
        IReadOnlyList<ChirpNarrationRange> ranges = inputFormat is not null && successor is not null
            ? parent.Parts.Select(part => verifiedReuse.SingleOrDefault(reuse => reuse.StartScalar == part.Range.StartScalar
                && reuse.EndScalarExclusive == part.Range.EndScalarExclusive) is { } accepted
                    ? part.Range with { ReusedReceiptSha256 = accepted.ReceiptSha256 } : part.Range).ToArray()
            : ChirpSentencePlanner.Plan(document.Text, verifiedReuse);
        var texts = ChirpNarrationPlanner.SliceAll(document.Text, ranges);
        List<ChirpArticlePlanPart> parts = [];
        for (int i = 0; i < ranges.Count; i++)
        {
            var wire = ranges[i].ReusedReceiptSha256 is null
                ? GoogleSpeechPlanCompiler.Compile(new SpeechPlan(parent.LanguageCode, [new SpeechText(texts[i])], ChirpSentencePlanner.Version),
                    new(parent.LanguageCode, parent.VoiceName, "MP3", 5_000)
                    { SentenceMarkers = inputFormat is null ? null : GoogleSpeechPlanCompiler.ExistingSentenceMarkers(texts[i]) }) : null;
            parts.Add(new(i + 1, ranges[i], wire?.PayloadSha256, wire?.Payload.Length ?? 0));
        }
        var proposal = parent with
        {
            PlannerVersion = inputFormat is null ? ChirpSentencePlanner.Version : SentenceMarkedArticleVersion,
            PlanSha256 = string.Empty,
            Parts = parts.AsReadOnly(),
            Refinement = null,
            SentenceSuccessor = successor,
            InputFormat = inputFormat
        };
        return proposal with { PlanSha256 = Hash(proposal) };
    }
}
