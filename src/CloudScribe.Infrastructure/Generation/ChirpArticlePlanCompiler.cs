using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Pure offline compilation. Production admission must independently verify source, receipts and every overlapping historical attempt.</summary>
public static class ChirpArticlePlanCompiler
{
    public static ChirpArticlePlanProposal Compile(DocumentSnapshot document, string language, string voice,
        IReadOnlyList<ChirpNarrationReuse>? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);
        if (document.Status != DocumentStatus.Active || document.Id == Guid.Empty || document.CurrentRevisionId is not { } revision || revision == Guid.Empty)
            throw new InvalidOperationException("Save an exact article revision before planning internal narration requests.");
        if (!voice.Contains("-Chirp3-HD-", StringComparison.Ordinal) || !voice.StartsWith(language + "-", StringComparison.Ordinal))
            throw new InvalidOperationException("This planning proposal applies only to Chirp 3 HD.");
        var ranges = ChirpNarrationPlanner.Plan(document.Text, reuse);
        var texts = ChirpNarrationPlanner.SliceAll(document.Text, ranges);
        List<ChirpArticlePlanPart> parts = [];
        for (int i = 0; i < ranges.Count; i++)
        {
            GoogleSpeechCompilation? compilation = ranges[i].ReusedReceiptSha256 is null
                ? GoogleSpeechPlanCompiler.Compile(new SpeechPlan(language, [new SpeechText(texts[i])], ChirpNarrationPlanner.Version),
                    new GoogleSpeechCompilationOptions(language, voice, "MP3", 5_000)) : null;
            parts.Add(new(i + 1, ranges[i], compilation?.PayloadSha256, compilation?.Payload.Length ?? 0));
        }
        var proposal = new ChirpArticlePlanProposal(document.Id, revision, ArticleAudioManifest.HashText(document.Text), document.Text.EnumerateRunes().Count(),
            language, voice, ChirpNarrationPlanner.Version, string.Empty, parts.AsReadOnly());
        return proposal with { PlanSha256 = Hash(proposal) };
    }

    /// <summary>Requires an exact recompile; a hash supplied by a proposal cannot authenticate itself.</summary>
    public static void RequireCurrent(ChirpArticlePlanProposal proposal, DocumentSnapshot current,
        IReadOnlyList<ChirpNarrationReuse>? verifiedReuse = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var compiled = Compile(current, proposal.LanguageCode, proposal.VoiceName, verifiedReuse);
        if (!string.Equals(compiled.PlanSha256, proposal.PlanSha256, StringComparison.Ordinal)
            || !JsonSerializer.SerializeToUtf8Bytes(compiled).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(proposal)))
            throw new InvalidOperationException("The article source, accepted coverage or exact internal request proposal changed.");
    }

    private static string Hash(ChirpArticlePlanProposal proposal) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(proposal)));

    /// <summary>Integrity only, never authority. Paid use still requires current independent admission and native-source verification.</summary>
    public static void RequireIntegrity(ChirpArticlePlanProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(proposal.PlanSha256, Hash(proposal with { PlanSha256 = string.Empty }), StringComparison.Ordinal))
            throw new InvalidOperationException("Stored article-plan provenance changed.");
    }
}
