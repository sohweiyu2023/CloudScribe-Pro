using System.Text;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Documents;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<NarrationArticleFixture> NarrationArticleAsync(Fixture f, bool whole = false, bool recovery = false, bool mixedRecovery = false)
    {
        var prior = await ChirpEvidenceAsync(f, 8).ConfigureAwait(false);
        string text = recovery ? string.Concat(Enumerable.Repeat(mixedRecovery ? "Alpha, beta, gamma, delta, epsilon, zeta.\u3002 " : "Alpha, beta, gamma, delta, epsilon, zeta. ", mixedRecovery ? 12 : 1)) + "https://example.test/private"
            : prior.Document.Text + "Read https://example.test/private and finish.";
        var source = await f.Library.CreateAsync("Private source", text, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var store = new EfNarrationCopyStore(f, new(f.Paths), prior.Clock);
        var copy = await store.PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash(source.Text), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var proposal = recovery ? ChirpArticlePlanCompiler.CompileSentences(copy.Narration, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam", inputFormat: new(1))
            : ChirpArticlePlanCompiler.Compile(copy.Narration, "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam");
        var history = new ChirpHistoricalRange[] { new(copy.Narration.Id, copy.Narration.CurrentRevisionId!.Value, 0,
            copy.Narration.Text.EnumerateRunes().Count(), Hash(copy.Narration.Text), []) };
        var article = new AdmittedChirpArticle(proposal,
            ChirpPlanLineageMapper.Map(copy.Narration.Text, proposal.Parts.Select(x => x.Range).ToArray(), history), [],
            proposal.Parts.Select(x => new ChirpSegmentAllowance(x.CoverageOrdinal, 1)).ToArray());
        int maximum = article.AllowedSegments.Count + (recovery ? 6 : 0);
        prior.Admission.Policy = prior.Admission.Policy! with
        {
            Articles = [article],
            SchemaVersion = whole ? 3 : 1,
            PriorSuccessorAttemptRows = whole ? [] : null,
            MaximumNewAttempts = maximum,
            MaximumAdditionalReservationMinorUnits = maximum,
            WholeArticleGeneration = whole ? new(Guid.NewGuid(), [proposal.PlanSha256]) : null,
            SentenceRecovery = recovery ? new(1, Guid.NewGuid(), ChirpBoundedSentenceSplit.Version, 2, 6, 6, 6, 6, []) : null
        };
        var logger = new NarrationArticleLogger();
        // Deliberately omit the optional store: direct construction must not bypass durable provenance.
        var service = new ChirpArticleService(f, prior.Admission, f.Paths, prior.Clock, logger: logger);
        return new(source, copy, prior with { Document = copy.Narration, Article = article, Service = service }, logger);
    }

    private static async Task EditNarrationSourceAsync(Fixture f, DocumentSnapshot source)
    {
        _ = await f.Library.SaveAsync(new(source.Id, source.Title, source.Text + " Changed after review.",
            source.ConcurrencyVersion, DocumentRevisionKind.Checkpoint), TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private sealed record NarrationArticleFixture(DocumentSnapshot Source, NarrationCopyPreparation Copy, ChirpEvidence Evidence,
        NarrationArticleLogger Logger);

    private sealed class NarrationArticleLogger : ILogger<ChirpArticleService>
    {
        public List<(EventId Event, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId, formatter(state, exception)));
    }
}
