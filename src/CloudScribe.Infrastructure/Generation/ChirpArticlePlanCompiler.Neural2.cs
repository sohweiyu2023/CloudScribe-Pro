using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public static partial class ChirpArticlePlanCompiler
{
    public const string Neural2Version = "google-neural2-compiled-boundaries-v1";

    private static ChirpArticlePlanProposal CompileNeural2(DocumentSnapshot document, string language, string voice,
        IReadOnlyList<ChirpNarrationReuse>? reuse)
    {
        var options = new GoogleSpeechCompilationOptions(language, voice, "MP3", 5_000);
        var ranges = GoogleCompiledTextPlanner.Plan(document.Text, options, reuse);
        var texts = ChirpNarrationPlanner.SliceAll(document.Text, ranges);
        List<ChirpArticlePlanPart> parts = [];
        for (int i = 0; i < ranges.Count; i++)
        {
            var wire = ranges[i].ReusedReceiptSha256 is null
                ? GoogleSpeechPlanCompiler.Compile(new SpeechPlan(language, [new SpeechText(texts[i])], Neural2Version), options) : null;
            parts.Add(new(i + 1, ranges[i], wire?.PayloadSha256, wire?.Payload.Length ?? 0));
        }
        var proposal = new ChirpArticlePlanProposal(document.Id, document.CurrentRevisionId!.Value,
            ArticleAudioManifest.HashText(document.Text), document.Text.EnumerateRunes().Count(), language, voice,
            Neural2Version, string.Empty, parts.AsReadOnly());
        return proposal with { PlanSha256 = Hash(proposal) };
    }
}
