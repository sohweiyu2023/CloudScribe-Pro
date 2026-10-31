using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.App.Composition;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleRecoveryProductionRoutingTests
{
    private static async Task<(ChirpArticleService Service, ChirpReservationResumePolicy Policy, ChirpReservedResumeDraft Draft)> PrepareResumeFixtureAsync(Fixture f)
    {
        await f.InitializeChirpAsync().ConfigureAwait(false);
        byte[] priceBytes = "{\"synthetic\":true}"u8.ToArray(); f.ChirpCompiler.Pricing = Convert.ToHexStringLower(SHA256.HashData(priceBytes));
        var old = f.CreateArticles(); var owner = new GoogleGenerationProductionSubmissionStateOwner(new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System, articles: old));
        var preparation = new ChirpArticlePreparationService(old, f.ChirpCompiler, owner, TimeProvider.System);
        var reviewed = await preparation.PrepareAsync(f.Selection, 1, TestContext.Current.CancellationToken).ConfigureAwait(false);
        await preparation.ApproveAsync(f.Selection, ChirpConfirm(reviewed), TestContext.Current.CancellationToken).ConfigureAwait(false);
        var active = (await owner.ResolveCurrentAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
        var catalog = await SeedResumeProviderAsync(f, active.SubmissionEnvelope, priceBytes).ConfigureAwait(false);
        f.UseResumeCatalog(catalog.Id);
        f.ChirpCompiler.Revision++; f.ChirpCompiler.FullRequestIdentity = true;
        var history = f.ReadChirpPolicy();
        var draft = await preparation.CreateReservedResumeDraftAsync(f.Selection, 1, history, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var inspected = draft.Retained;
        var policy = new ChirpReservationResumePolicy(1, Guid.NewGuid(), history.AppSha256, history.InfrastructureSha256, history.ProfileRootSha256,
            new string('d', 64), catalog.ExpiresAtUtc.AddMinutes(-1), history, inspected.AuthorizationId, inspected.OriginalAttemptRowSha256,
            inspected.CleanQueueRowSha256, inspected.FinancialSha256, inspected.OriginalApprovedAtUtc, inspected.OriginalEnvelope,
            draft.CandidateEnvelope, catalog.Id, catalog.ExpiresAtUtc, inspected.ProviderEvidenceSha256,
            draft.SelectionSha256, draft.RuntimeEvidenceSha256, draft.RequestIdentitySha256, 1, 0, 0, draft.PriorMappingSha256);
        return (f.CreateResumeArticles(new ResumeAdmission(policy)), policy, draft);
    }

    private static async Task<CloudScribe.Application.Providers.StoredProviderCapabilitySnapshot> SeedResumeProviderAsync(Fixture f,
        GoogleGenerationSubmissionEnvelope envelope, byte[] price)
    {
        var reference = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, envelope.AccountId, "Synthetic Google",
            new CredentialReference(envelope.CredentialReferenceId), "google-tts-v1", "global", new Uri("https://texttospeech.googleapis.com/"));
        await new EfProviderAccountStore(f, TimeProvider.System).CreateAsync(reference, true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var catalog = await new EfProviderCapabilitySnapshotStore(f).SaveAsync(new(reference, now, envelope.CapabilityProvenanceId,
            [new(GoogleGenerationProvider.SynthesizeOperationStableId, ProviderCapabilityState.Supported, ProviderLifecycleState.Available)]), now.AddMinutes(30), TestContext.Current.CancellationToken).ConfigureAwait(false);
        await new GoogleGenerationProjectAuthorizationStore(f).SaveVerifiedAsync(new(envelope.AccountId, "synthetic-project", envelope.VoiceName,
            envelope.CredentialReferenceId, envelope.CapabilityProvenanceId, "google-tts-v1", "global", reference.EndpointOrigin!.AbsoluteUri, true, now, now.AddHours(1)), TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var db = f.CreateDbContext(); var pricing = Guid.NewGuid();
        db.PricingCatalogSnapshots.Add(new PricingCatalogSnapshotEntity
        {
            Id = pricing,
            Sha256 = envelope.PricingProvenanceId,
            CatalogBytes = price,
            TrustState = (int)PricingCatalogTrustState.ValidUnsigned,
            SourceLabel = "Synthetic",
            CapturedAtUnixMilliseconds = now.ToUnixTimeMilliseconds()
        });
        db.PricingCatalogActivations.Add(new PricingCatalogActivationEntity { SnapshotId = pricing, Reason = "Synthetic", OccurredAtUnixMilliseconds = now.ToUnixTimeMilliseconds() });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false); return catalog;
    }

    private sealed record ResumeAdmission(ChirpReservationResumePolicy Policy) : IChirpReservationResumeAdmissionStore
    { public Task<ChirpReservationResumePolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default) => Task.FromResult<ChirpReservationResumePolicy?>(Policy); }

    private sealed partial class Fixture
    {
        public string ResumeDraftPath => Path.Combine(_root, "private-resume-review.json");
        public void UseResumeCatalog(Guid id) => Selection = Selection with { CapabilityEvidenceId = id.ToString("D"), CapturedAtUtc = DateTimeOffset.UtcNow };
        public ChirpArticleAdmissionPolicy ReadChirpPolicy() => JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllText(Path.Combine(_root, "install", "chirp-article-admission-policy.json")))!;
        public ChirpArticleService CreateResumeArticles(IChirpReservationResumeAdmissionStore store) => new(this,
            new InstalledChirpArticleAdmissionStore(Path.Combine(_root, "install", "app"), _paths, TimeProvider.System), _paths, TimeProvider.System, store);
    }
}
