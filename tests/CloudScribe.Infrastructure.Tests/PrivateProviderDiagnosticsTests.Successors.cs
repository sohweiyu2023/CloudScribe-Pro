using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    private static GenerationProviderRequest DiagnosticChild(Fixture fixture) => new(fixture.Request.ProviderStableId,
        fixture.Request.OperationStableId, fixture.Request.AccountId,
        ChirpArticleService.KeyPrefix + new string('a', 64) + ":2:" + Guid.NewGuid().ToString("N"), fixture.Request.CompiledPayload, "mp3");

    [Theory]
    [InlineData("off")]
    [InlineData("other-object")]
    [InlineData("wrong-reference")]
    [InlineData("expired")]
    public async Task DiagnosticChildCannotDispatchWithoutExactAttachedUnexpiredTicket(string state)
    {
        using var fixture = new Fixture(); var child = DiagnosticChild(fixture);
        if (!string.Equals(state, "off", StringComparison.Ordinal))
        {
            var binding = string.Equals(state, "wrong-reference", StringComparison.Ordinal)
                ? fixture.Binding with { CredentialReferenceId = "wrong-reference" } : fixture.Binding;
            fixture.Store.Arm(binding, true);
            var attached = string.Equals(state, "other-object", StringComparison.Ordinal)
                ? new GenerationProviderRequest(child.ProviderStableId, child.OperationStableId, child.AccountId, child.IdempotencyKey, child.CompiledPayload, child.OutputFormat) : child;
            fixture.Store.Attach(attached, binding);
        }
        if (string.Equals(state, "expired", StringComparison.Ordinal)) fixture.Clock.Now += TimeSpan.FromMinutes(11);
        Assert.Throws<PrivateDiagnosticNotReadyException>(() => fixture.Provider.RequirePrivateDiagnosticsReady(child));
        await Assert.ThrowsAsync<PrivateDiagnosticNotReadyException>(() => fixture.Provider.SubmitAsync(child, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Empty(fixture.Store.ReadRecords());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentLossDuringCredentialAwaitPreventsDiagnosticHttpDispatch(bool expire)
    {
        using var fixture = new Fixture(); var child = DiagnosticChild(fixture);
        fixture.Store.Arm(fixture.Binding, true); fixture.Store.Attach(child, fixture.Binding);
        var credential = new DelayedDiagnosticCredential();
        using var http = new HttpClient(fixture.Handler, disposeHandler: false);
        var transport = new GoogleGenerationHttpTransport(http, credential, new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: fixture.Store);
        var provider = new GoogleGenerationProvider(new("synthetic-account", "synthetic-reference", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
        provider.RequirePrivateDiagnosticsReady(child);
        Task<GenerationProviderResponse> sending = provider.SubmitAsync(child, TestContext.Current.CancellationToken);
        await credential.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (expire) fixture.Clock.Now += TimeSpan.FromMinutes(11); else fixture.Store.Disarm();
        credential.Release.SetResult("SYNTHETIC_SECRET");
        await Assert.ThrowsAsync<PrivateDiagnosticNotReadyException>(() => sending);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Empty(fixture.Store.ReadRecords());
    }

    [Fact]
    public async Task ExactChildRetainsUnknownErrorOnceAndRootCompatibilityIsUnchanged()
    {
        using var fixture = new Fixture(); var child = DiagnosticChild(fixture);
        fixture.Store.Arm(fixture.Binding, true); fixture.Store.Attach(child, fixture.Binding);
        fixture.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "An unfamiliar provider explanation remains useful." } });
        fixture.Provider.RequirePrivateDiagnosticsReady(child);
        var response = await fixture.Provider.SubmitAsync(child, TestContext.Current.CancellationToken);
        await fixture.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        Assert.Equal("google-http-400", response.DiagnosticCode);
        Assert.Contains("unfamiliar", Assert.Single(fixture.Store.ReadRecords()).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PrivateDiagnosticNotReadyException>(() => fixture.Provider.SubmitAsync(child, TestContext.Current.CancellationToken));
        var root = new GenerationProviderRequest(child.ProviderStableId, child.OperationStableId, child.AccountId,
            ChirpArticleService.KeyPrefix + new string('b', 64) + ":1", child.CompiledPayload, child.OutputFormat);
        fixture.Provider.RequirePrivateDiagnosticsReady(root);
        await fixture.Provider.SubmitAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    private sealed class DelayedDiagnosticCredential : ITransientCredentialResolver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<string> ResolveAccessTokenAsync(string credentialReferenceId, CancellationToken cancellationToken)
        { Entered.SetResult(); return await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
    }
}
