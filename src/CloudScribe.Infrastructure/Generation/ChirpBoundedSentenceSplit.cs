using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Inert, lossless binary proposal only. No ledger, admission, dispatch, retry or policy-writing path calls this compiler.</summary>
public static class ChirpBoundedSentenceSplit
{
    public const string Version = "chirp3-bounded-natural-binary-v1";
    public static ChirpSentenceSplitProposal Compile(string source, ChirpNarrationRange parent,
        GoogleSpeechCompilationOptions options, ChirpSentenceSplitEvidence evidence,
        ChirpSentenceSplitGrant grant, ChirpSentenceSplitState state,
        Func<ReadOnlyMemory<byte>, long> estimateMaximumMinorUnits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(estimateMaximumMinorUnits);
        RequireEvidenceAndAllowance(source, evidence, grant, state);
        string original = ChirpNarrationPlanner.Slice(source, parent);
        RequireParentWire(original, options, evidence);
        int cut = FindNaturalCut(source, original, parent);
        var left = MakeRange(original, parent.StartScalar, parent.StartScalar, cut);
        var right = MakeRange(original, parent.StartScalar, cut, parent.EndScalarExclusive);
        var parts = new[] { CompileChild(left), CompileChild(right) };
        long reserved = checked(state.ReservedMinorUnitsUsed + checked(parts[0].MaximumMinorUnits + parts[1].MaximumMinorUnits));
        if (reserved > grant.MaximumAdditionalMinorUnits
            || !string.Equals(string.Concat(parts.Select(x => ChirpNarrationPlanner.Slice(source, x.Range))), original, StringComparison.Ordinal))
            throw new InvalidOperationException("Subdivision exceeds its retained allowance or changes source coverage.");
        return new(grant.GrantId, evidence.AuthorizationId, Array.AsReadOnly(parts), state.Depth + 1,
            checked(state.ChildAttemptsUsed + 2), reserved);

        ChirpSentenceSplitPart CompileChild(ChirpNarrationRange range)
        {
            string text = ChirpNarrationPlanner.Slice(source, range);
            var markers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(text);
            var wire = GoogleSpeechPlanCompiler.Compile(new(options.LanguageCode, [new SpeechText(text)], "bounded-natural-sentence-split-v1"),
                options with { SentenceMarkers = markers });
            if (string.Equals(wire.PayloadSha256, evidence.PayloadSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Subdivision cannot replay the failed request.");
            long maximum = estimateMaximumMinorUnits(wire.Payload);
            if (maximum < 1 || maximum > grant.MaximumAdditionalMinorUnits)
                throw new InvalidOperationException("An exact conservative per-request estimate is required.");
            return new(range, wire, maximum);
        }
    }

    private static ChirpNarrationRange MakeRange(string original, int parentStart, int start, int end)
    {
        string value = string.Concat(original.EnumerateRunes().Skip(start - parentStart).Take(end - start).Select(x => x.ToString()));
        return new(start, end, ArticleAudioManifest.HashText(value), null);
    }

    private static int FindNaturalCut(string source, string original, ChirpNarrationRange parent)
    {
        // Production scanner retains closing punctuation, whitespace and text elements.
        var clauses = ChirpClauseSubdivision.Plan(source, parent);
        var runes = original.EnumerateRunes().ToArray();
        var cuts = clauses.Take(clauses.Count - 1).Select(x => x.EndScalarExclusive)
            .Where(x => runes.Take(x - parent.StartScalar).Any(Rune.IsLetterOrDigit)
                && runes.Skip(x - parent.StartScalar).Any(Rune.IsLetterOrDigit)).ToArray();
        if (cuts.Length == 0) throw new InvalidOperationException("No existing natural boundary yields two speakable unchanged source ranges.");
        return cuts.OrderBy(x => Math.Abs(2L * x - parent.StartScalar - parent.EndScalarExclusive)).ThenBy(x => x).First();
    }

    private static void RequireParentWire(string original, GoogleSpeechCompilationOptions options, ChirpSentenceSplitEvidence evidence)
    {
        var wire = GoogleSpeechPlanCompiler.Compile(new(options.LanguageCode, [new SpeechText(original)], "bounded-natural-sentence-split-v1"),
            options with { SentenceMarkers = GoogleSpeechPlanCompiler.ExistingSentenceMarkers(original) });
        if (!string.Equals(wire.PayloadSha256, evidence.PayloadSha256, StringComparison.Ordinal) || wire.Payload.Length != evidence.PayloadBytes)
            throw new InvalidOperationException("The terminal evidence does not identify this exact source and sentence format.");
    }

    private static void RequireEvidenceAndAllowance(string source, ChirpSentenceSplitEvidence evidence,
        ChirpSentenceSplitGrant grant, ChirpSentenceSplitState state)
    {
        if (evidence.AuthorizationId == Guid.Empty || evidence.RootAuthorizationId == Guid.Empty
            || evidence.HttpStatus != 400 || evidence.ErrorStatus != GoogleErrorStatus.InvalidArgument
            || evidence.MessageHint != GoogleValidationMessageHint.SentenceLengthReported
            || !evidence.Consumed || !evidence.SubmissionAttempted || evidence.Unresolved
            || evidence.HasPendingWork || evidence.HasAcceptedOrPendingOutput
            || !ChirpPolicyValidation.IsHash(evidence.PayloadSha256) || evidence.PayloadBytes is < 1 or > 5000
            || !ChirpPolicyValidation.IsHash(evidence.AttemptRowSha256) || !ChirpPolicyValidation.IsHash(evidence.QueueRowSha256))
            throw new InvalidOperationException("Exact terminal sentence-length evidence is required; uncertainty never permits subdivision.");
        if (grant.Version != 1 || grant.GrantId == Guid.Empty || grant.RootAuthorizationId != evidence.RootAuthorizationId
            || !ChirpPolicyValidation.IsHash(grant.SourceApprovalSha256)
            || !string.Equals(grant.SourceSha256, ArticleAudioManifest.HashText(source), StringComparison.Ordinal)
            || grant.MaximumDepth < 1 || grant.MaximumDepth >= source.EnumerateRunes().Count() || grant.MaximumChildAttempts < 2
            || grant.MaximumAdditionalMinorUnits < 2 || state.Depth < 0 || state.Depth >= grant.MaximumDepth
            || state.ChildAttemptsUsed < 0 || state.ChildAttemptsUsed > grant.MaximumChildAttempts - 2
            || state.ReservedMinorUnitsUsed < 0 || state.ReservedMinorUnitsUsed > grant.MaximumAdditionalMinorUnits - 2
            || state.Depth == 0 && evidence.AuthorizationId != grant.RootAuthorizationId)
            throw new InvalidOperationException("The independently admitted subdivision allowance is absent, changed or exhausted.");
    }
}
