using CloudScribe.App.Composition;
using CloudScribe.Application.Pricing;
using CloudScribe.Application.Security;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Architecture.Tests;

public sealed partial class GoogleGenerationProductionCompileAndPrepareServiceTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("model")]
    [InlineData("capability")]
    public async Task ChirpRuntimeRechecksProjectAuthorityBeforeCredentialsSpendOrTransport(string change)
    {
        var account = CreatePersistedAccount("credential-current");
        var production = new GoogleGenerationProductionEvidenceResolver(new AccountStore(account),
            new CapabilityStore(CreatePersistedCapability(account.Reference, "capability-1")), new FixedTimeProvider(Now));
        var compile = CreateService(new(), account, "capability-1");
        var pending = await compile.CompileForReviewAsync(CreateEvidence("credential-current", "capability-1"), TestContext.Current.CancellationToken);
        var envelope = pending.Envelope with { ChirpAuthorizationId = Guid.NewGuid() };
        var spend = new ChirpBlockedSpend(); var vault = new ChirpBlockedVault(); var credential = new ChirpBlockedCredential();
        using var http = new HttpClient(new ChirpBlockedHttp());
        var project = new GoogleGenerationProjectAuthorizationEvidence("account-1", "project-1", "model-1", "credential-current",
            "capability-1", "google-tts-v1", "global", Endpoint.AbsoluteUri, true, Now.AddMinutes(-5), Now.AddHours(1));
        var projects = new ChirpRuntimeProject(change switch
        {
            "missing" => null,
            "revoked" => project with { Authorized = false },
            "model" => project with { ModelId = "changed-model" },
            _ => project with { CapabilityProvenanceId = "changed-capability" }
        });
        var resolver = new GoogleGenerationProductionRuntimeEvidenceResolver(production,
            new(new FixedTimeProvider(Now), new(vault)), new(spend), new ChirpRuntimePricing(), new(http, credential, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now), projects);
        var snapshot = pending.Snapshot with { PricingApproved = true };
        var request = GoogleGenerationProductionRuntimeRequestFactory.Create(GoogleGenerationSpendAuthorization.Create(envelope,
            pending.Currency, pending.Scale, pending.CurrentEstimateMinorUnits, pending.CurrentEstimateMinorUnits), snapshot, pending.CurrentEstimateMinorUnits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, projects.Reads); Assert.Equal(0, vault.Reads); Assert.Equal(0, spend.Reads); Assert.Equal(0, credential.Reads);
    }

    private sealed class ChirpRuntimeProject(GoogleGenerationProjectAuthorizationEvidence? value) : IGoogleGenerationProjectAuthorizationStore
    {
        public int Reads { get; private set; }
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadCurrentAsync(string accountId, string projectId, string modelId, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(value); }
        public Task<GoogleGenerationProjectAuthorizationEvidence?> LoadSingleCurrentForProjectAsync(string accountId, string projectId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveVerifiedAsync(GoogleGenerationProjectAuthorizationEvidence evidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ChirpBlockedVault : ICredentialVault
    {
        public int Reads { get; private set; }
        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        { Reads++; throw new InvalidOperationException("Synthetic vault must not be reached after project revocation."); }
        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ChirpBlockedSpend : IGoogleGenerationSpendAuthorizationStore
    {
        public int Reads { get; private set; }
        public Task<GoogleGenerationSpendAuthorization?> LoadApprovedAsync(GoogleGenerationSubmissionEnvelope envelope, CancellationToken cancellationToken = default)
        { Reads++; throw new InvalidOperationException("Synthetic spend must not be reached after project revocation."); }
        public Task SaveApprovedAsync(GoogleGenerationSpendAuthorization authorization, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ChirpBlockedCredential : ITransientCredentialResolver
    {
        public int Reads { get; private set; }
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { Reads++; throw new InvalidOperationException("Synthetic provider token must never be requested."); }
    }
    private sealed class ChirpBlockedHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP call is permitted in this offline revocation test.");
    }
    private sealed class ChirpRuntimePricing : IPricingCatalogHistoryStore
    {
        public Task<PricingCatalogSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<PricingCatalogSnapshot?>(
            new(Guid.NewGuid(), "pricing-1", 1, PricingCatalogTrustState.SignatureVerified, new(PricingCatalogSourceKind.ImportedFile, "synthetic pricing"), Now, null));
        public Task<PricingCatalogSnapshot> SaveSnapshotAsync(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogTrustState trustState, PricingCatalogSource source, string? signatureKeyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PricingCatalogSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PricingCatalogActivation>> ListActivationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PricingCatalogActivation> ActivateAsync(PricingCatalogActivationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
