using CloudScribe.Application.Generation;
using CloudScribe.Application.Providers;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence.Entities;
using CloudScribe.Infrastructure.Providers;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<ReservedResumeFixture> ReservedResumeAsync(Fixture f, bool successor = false)
    {
        await f.InitializeAsync().ConfigureAwait(false);
        byte[] price = "{\"synthetic\":\"pricing\"}"u8.ToArray(); string priceSha = Hash(System.Text.Encoding.UTF8.GetString(price));
        var failed = successor ? await CreateSuccessorFixtureAsync(f, pricingSha256: priceSha).ConfigureAwait(false) : null;
        var old = failed?.Evidence ?? await ChirpEvidenceAsync(f, 2).ConfigureAwait(false);
        var intent = failed?.Intent ?? ChirpIntent(old); intent = intent with { Envelope = intent.Envelope with { PricingProvenanceId = priceSha } };
        var preview = await old.Service.PrepareAsync(intent, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var reserved = await old.Service.ApproveAsync(ChirpConfirmation(preview), TestContext.Current.CancellationToken).ConfigureAwait(false);
        old.Clock.Now = old.Admission.Policy!.ExpiresAtUtc.AddSeconds(1);
        var catalog = await SeedReservedProviderAsync(f, old.Clock.GetUtcNow(), intent.Envelope, price).ConfigureAwait(false);
        var inspected = await old.Service.InspectReservedResumeAdmissionAsync(reserved.AuthorizationId, old.Admission.Policy,
            catalog.Id, catalog.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.False(inspected.GrantsAuthority); Assert.Equal(successor ? 150 : 149, inspected.ProjectCommittedMinorUnits);
        var policy = new ChirpReservationResumePolicy(1, Guid.NewGuid(), new string('a', 64), new string('b', 64),
            old.Admission.Policy.ProfileRootSha256, new string('d', 64), catalog.ExpiresAtUtc.AddMinutes(-1), old.Admission.Policy,
            reserved.AuthorizationId, inspected.OriginalAttemptRowSha256, inspected.CleanQueueRowSha256, inspected.FinancialSha256,
            inspected.OriginalApprovedAtUtc, inspected.OriginalEnvelope, inspected.OriginalEnvelope with { RequestRevision = 2 },
            catalog.Id, catalog.ExpiresAtUtc, inspected.ProviderEvidenceSha256, new string('e', 64), new string('f', 64), new string('1', 64), 1, 0, 0);
        var store = new SyntheticResumeAdmission { Policy = policy };
        var service = new ChirpArticleService(f, old.Admission, f.Paths, old.Clock, store);
        return new(old, service, store, reserved, intent with { Envelope = policy.ExecutionEnvelope with { ChirpAuthorizationId = null } });
    }

    private static async Task<StoredProviderCapabilitySnapshot> SeedReservedProviderAsync(Fixture f, DateTimeOffset now,
        GoogleGenerationSubmissionEnvelope envelope, byte[] price)
    {
        var reference = new ProviderAccountReference(GoogleGenerationProvider.StableProviderId, envelope.AccountId, "Synthetic Google",
            new CredentialReference(envelope.CredentialReferenceId), "google-tts-v1", "global", new Uri("https://texttospeech.googleapis.com/"));
        await new EfProviderAccountStore(f, new ChirpClock { Now = now }).CreateAsync(reference, true, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var catalog = await new EfProviderCapabilitySnapshotStore(f).SaveAsync(new(reference, now, envelope.CapabilityProvenanceId,
            [new(GoogleGenerationProvider.SynthesizeOperationStableId, ProviderCapabilityState.Supported, ProviderLifecycleState.Available)]),
            now.AddMinutes(30), TestContext.Current.CancellationToken).ConfigureAwait(false);
        await new GoogleGenerationProjectAuthorizationStore(f).SaveVerifiedAsync(new(envelope.AccountId, "synthetic-project", envelope.VoiceName,
            envelope.CredentialReferenceId, envelope.CapabilityProvenanceId, reference.EndpointId!, reference.RegionId!, reference.EndpointOrigin!.AbsoluteUri,
            true, DateTimeOffset.UtcNow, now.AddHours(1)), TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var db = f.CreateDbContext(); Guid pricing = Guid.NewGuid();
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return catalog;
    }

    private sealed class SyntheticResumeAdmission : IChirpReservationResumeAdmissionStore
    {
        public ChirpReservationResumePolicy? Policy { get; set; }
        public int Reads { get; set; }
        public int RevokeAt { get; set; } = int.MaxValue;
        public Task<ChirpReservationResumePolicy?> LoadVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(++Reads >= RevokeAt ? null : Policy);
    }

    private sealed record ReservedResumeFixture(ChirpEvidence Old, ChirpArticleService Service, SyntheticResumeAdmission Admission,
        ChirpApprovedAttempt Reserved, ChirpRequestIntent Intent);

    private static Task<ChirpRequestReview> ResumeReviewAsync(ReservedResumeFixture r, ChirpArticleService? service = null) =>
        (service ?? r.Service).PrepareReservedResumeAsync(r.Intent, r.Admission.Policy!.SelectionSha256, r.Admission.Policy.RuntimeEvidenceSha256,
            r.Admission.Policy.CatalogSnapshotId, r.Admission.Policy.RequestIdentitySha256, TestContext.Current.CancellationToken);
}
