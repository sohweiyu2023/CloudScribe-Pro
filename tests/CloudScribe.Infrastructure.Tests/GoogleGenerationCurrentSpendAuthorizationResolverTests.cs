using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleGenerationCurrentSpendAuthorizationResolverTests
{
    [Fact]
    public async Task ExactDurableApprovalResolvesForCurrentEstimate()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            6,
            approvedEstimateMinorUnits: 1_250_000,
            authorizedMaximumMinorUnits: 1_500_000);
        RecordingStore store = new(authorization);
        GoogleGenerationCurrentSpendAuthorizationResolver resolver = new(store);

        GoogleGenerationSpendAuthorization resolved = await resolver.ResolveAsync(
            envelope,
            "USD",
            6,
            1_250_000,
            cancellationToken).ConfigureAwait(true);

        Assert.Same(authorization, resolved);
        Assert.Equal(1, store.LoadCount);
        Assert.Same(envelope, store.LastEnvelope);
        Assert.Equal(cancellationToken, store.LastCancellationToken);
    }

    [Fact]
    public async Task MissingDurableApprovalFailsClosed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        RecordingStore store = new(null);
        GoogleGenerationCurrentSpendAuthorizationResolver resolver = new(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            envelope,
            "USD",
            6,
            1_250_000,
            cancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ChangedCurrentEstimateFailsClosed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            6,
            approvedEstimateMinorUnits: 1_250_000,
            authorizedMaximumMinorUnits: 1_500_000);
        GoogleGenerationCurrentSpendAuthorizationResolver resolver = new(new RecordingStore(authorization));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            envelope,
            "USD",
            6,
            1_250_001,
            cancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ChangedCurrencyFailsClosed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GoogleGenerationSubmissionEnvelope envelope = CreateEnvelope();
        GoogleGenerationSpendAuthorization authorization = GoogleGenerationSpendAuthorization.Create(
            envelope,
            "USD",
            6,
            approvedEstimateMinorUnits: 1_250_000,
            authorizedMaximumMinorUnits: 1_500_000);
        GoogleGenerationCurrentSpendAuthorizationResolver resolver = new(new RecordingStore(authorization));

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            envelope,
            "EUR",
            6,
            1_250_000,
            cancellationToken)).ConfigureAwait(true);
    }

    private static GoogleGenerationSubmissionEnvelope CreateEnvelope() => new(
        AccountId: "account-1",
        CredentialReferenceId: "cred-1",
        CapabilityProvenanceId: "cap-v1",
        PricingProvenanceId: "price-v1",
        RequestRevision: 7,
        VoiceName: "en-US-TestVoice",
        AudioEncoding: "MP3",
        CompiledPayloadSha256: new string('a', 64),
        CompiledPayloadBytes: 512);

    private sealed class RecordingStore(GoogleGenerationSpendAuthorization? authorization)
        : IGoogleGenerationSpendAuthorizationStore
    {
        public int LoadCount { get; private set; }

        public GoogleGenerationSubmissionEnvelope? LastEnvelope { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task SaveApprovedAsync(
            GoogleGenerationSpendAuthorization value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(
            GoogleGenerationSubmissionEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastEnvelope = envelope;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(authorization);
        }
    }
}
