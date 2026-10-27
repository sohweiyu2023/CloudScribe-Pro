using System.Net;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Theory]
    [InlineData(400, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task MalformedUnicodeErrorRetainsConsumedGrantHoldsAndStickyDispatch(int status, bool unresolved)
    {
        using var f = new Fixture(); await f.InitializeAsync(); var e = await RecoveryEvidenceAsync(f);
        var (admission, budget) = await CreateInstalledBudgetAsync(f, e);
        var service = new GoogleExplicitRecoveryService(f, TimeProvider.System, budget, admission);
        var preview = await service.PrepareAsync(e.Intent, 3, TestContext.Current.CancellationToken);
        var approved = await service.ApproveAsync(Confirmation(preview), TestContext.Current.CancellationToken);
        var envelope = e.Intent.Envelope with { RecoveryAuthorizationId = approved.AuthorizationId };
        using var handler = new DiagnosticErrorHandler(status);
        using var http = new HttpClient(handler);
        var provider = new GoogleGenerationProvider(new GoogleGenerationAccount(envelope.AccountId, envelope.CredentialReferenceId,
            new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"),
            new GoogleGenerationHttpTransport(http, new DiagnosticCredential(), new Uri("https://texttospeech.googleapis.com")));
        var persisted = new GoogleGenerationPersistedQueueStateStore(f);
        var queue = new GoogleGenerationQueueCoordinator((request, _, token) => provider.SubmitAsync(request, token));
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, persisted.LoadAsync, persisted.SaveAsync,
            (claim, token) => service.TryClaimAuthorizedAsync(envelope, e.Intent.ProjectId, "USD", 2, 3, claim, token), persisted.SaveOutcomeAsync);
        var request = new GenerationProviderRequest(GoogleGenerationProvider.StableProviderId, GoogleGenerationProvider.SynthesizeOperationStableId,
            envelope.AccountId, approved.IdempotencyKey, e.Intent.CompiledPayload, "MP3");
        var outcome = await bound.ProcessDurableAsync(request, RecoveryTrust(e), true, true, true, true, TestContext.Current.CancellationToken);
        Assert.Equal(unresolved, outcome.RequiresReconciliation);
        Assert.Equal(GoogleErrorStatus.Unknown, outcome.Response!.ErrorStatus);
        var saved = await persisted.LoadAsync(request.AccountId, request.OperationStableId, request.IdempotencyKey, TestContext.Current.CancellationToken);
        Assert.True(saved!.SubmissionAttempted);
        Assert.Equal(unresolved, saved.UnresolvedSubmission);
        Assert.Null(saved.ProviderRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.ProcessDurableAsync(request, RecoveryTrust(e), true, true, true, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
        await AssertConcreteBudgetAsync(f, e, budget, 253, 67);
        Assert.Equal(e.Previous, await persisted.LoadAsync(e.Previous.AccountId, e.Previous.OperationStableId, e.Previous.IdempotencyKey, TestContext.Current.CancellationToken));
        Assert.Equal(e.OldSpend, await new GoogleGenerationSpendAuthorizationStore(f, TimeProvider.System).LoadApprovedAsync(e.Intent.Envelope, TestContext.Current.CancellationToken));
        using var context = f.CreateDbContext();
        Assert.Equal(1, await context.Database.SqlQueryRaw<long>("SELECT SUM(Consumed) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await context.Database.SqlQueryRaw<long>("SELECT SUM(ReservedMinorUnits) AS Value FROM google_recovery_attempts").SingleAsync(TestContext.Current.CancellationToken));
    }

    private sealed class DiagnosticCredential : ITransientCredentialResolver
    {
        public ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken) => ValueTask.FromResult("synthetic-token");
    }
    private sealed class DiagnosticErrorHandler(int status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"status\":\"\\uD800\"}}") });
        }
    }
}
