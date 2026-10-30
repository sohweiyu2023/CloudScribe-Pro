using System.Net;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class GoogleCatalogBootstrapTests
{
    [Fact]
    public async Task CredentialFailureRecordsItsStageWithoutMessageOrExtraProviderCalls()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        fixture.Vault.ReadFailure = new InvalidOperationException("private-key bearer article-content");
        int before = fixture.Handler.Calls;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before, fixture.Handler.Calls);
        Assert.Contains(fixture.Bootstrap.Messages, x => x.Contains("failed at CredentialIdentity", StringComparison.Ordinal));
        fixture.AssertPrivateValuesAbsent();
    }

    [Fact]
    public async Task ProbeRejectionRetainsActualHttpStatusAndOriginalExceptionType()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        fixture.Handler.RejectProbe = true;
        int before = fixture.Handler.Calls;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, fixture.Handler.Calls);
        Assert.Equal(403, GoogleCatalogRefreshDiagnostics.HttpStatus(failure));
        Assert.Equal(GoogleCatalogRefreshPhase.ProbeValidation, GoogleCatalogRefreshDiagnostics.ReportedPhase(failure, null));
        Assert.Contains(fixture.Probe.Messages, x => x.Contains("HTTP 403", StringComparison.Ordinal));
        Assert.Contains(fixture.Probe.Messages, x => x.Contains("failed at ProbeValidation", StringComparison.Ordinal));
        fixture.AssertPrivateValuesAbsent();
    }

    [Fact]
    public async Task MalformedCatalogRecordsParseFailureAndDoesNotProbe()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        fixture.Handler.MalformedCatalog = true;
        int before = fixture.Handler.Calls;

        await Assert.ThrowsAnyAsync<JsonException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before + 1, fixture.Handler.Calls);
        Assert.Contains(fixture.Catalog.Messages, x => x.Contains("failed at CatalogParse", StringComparison.Ordinal));
        Assert.Contains(fixture.Catalog.Messages, x => x.Contains("invalid-json", StringComparison.Ordinal));
        fixture.AssertPrivateValuesAbsent();
    }

    [Theory]
    [InlineData("capability", "CapabilitySave")]
    [InlineData("project", "ProjectSave")]
    [InlineData("voices", "VoiceAuthorizationsSave")]
    public async Task PersistenceFailuresIdentifyTheSubstageWithoutRetry(string failAt, string expected)
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        fixture.Stores.FailAt = failAt;
        int before = fixture.Handler.Calls;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, fixture.Handler.Calls);
        Assert.Contains(fixture.Bootstrap.Messages, x => x.Contains("failed at " + expected, StringComparison.Ordinal));
        Assert.Equal(3, fixture.Vault.Writes);
        fixture.AssertPrivateValuesAbsent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggingFailureDoesNotAbortSuccessOrReplaceTheOriginalFailure(bool failIsEnabled)
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        foreach (var target in new ICaptureLogger[] { fixture.Bootstrap, fixture.Catalog, fixture.Probe })
            target.ThrowDuringLogging(failIsEnabled);
        int before = fixture.Handler.Calls;
        await fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before + 2, fixture.Handler.Calls);
        var original = new InvalidOperationException("original-private-failure");
        fixture.Vault.ReadFailure = original;
        var propagated = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));
        Assert.Same(original, propagated);
        Assert.Equal(before + 2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task OneObservedRefreshIdCorrelatesBootstrapCatalogProbeAndPersistence()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync();
        fixture.Clear();
        Guid id = Guid.NewGuid();
        await fixture.Service.RefreshConfiguredAsync(id, TestContext.Current.CancellationToken);
        foreach (var fields in fixture.Bootstrap.States.Concat(fixture.Catalog.States).Concat(fixture.Probe.States))
            Assert.Equal(id, fields.Single(x => string.Equals(x.Key, "RefreshId", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public async Task MissingSavedMetadataHasActionableCategoryWithoutAnyProviderCall()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.ConfigureAsync(); fixture.Clear();
        await fixture.Vault.DeleteAsync(new CloudScribe.Providers.Abstractions.CredentialReference("synthetic-credential.google-service-account-metadata"), TestContext.Current.CancellationToken);
        int before = fixture.Handler.Calls;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshConfiguredAsync(TestContext.Current.CancellationToken));
        Assert.Equal("saved-credential-unavailable", GoogleCatalogRefreshDiagnostics.Category(error));
        Assert.Equal(before, fixture.Handler.Calls);
        Assert.Contains(fixture.Bootstrap.Messages, line => line.Contains("CredentialIdentity", StringComparison.Ordinal) && line.Contains("saved-credential-unavailable", StringComparison.Ordinal));
        fixture.AssertPrivateValuesAbsent();
    }

    private sealed class DiagnosticFixture : IDisposable
    {
        public Clock Clock { get; } = new();
        public Vault Vault { get; } = new();
        public ProviderHandler Handler { get; } = new();
        public Stores Stores { get; }
        public CaptureLogger<GoogleTextToSpeechCatalogBootstrapService> Bootstrap { get; } = new();
        public CaptureLogger<GoogleVoiceCatalogClient> Catalog { get; } = new();
        public CaptureLogger<GoogleSynthesisCapabilityProbe> Probe { get; } = new();
        public GoogleTextToSpeechCatalogBootstrapService Service { get; }
        private readonly HttpClient _client;

        public DiagnosticFixture()
        {
            Stores = new(Clock);
            _client = new(Handler);
            var resolver = new Resolver();
            Service = new(new(Vault), new(_client, resolver, Catalog), new(_client, resolver, Clock, Probe),
                Stores, Stores, Stores, Stores, Clock, Bootstrap);
        }

        public Task<GoogleTextToSpeechCatalogBootstrapResult> ConfigureAsync()
        {
            string key = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "synthetic-project",
                client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
                private_key_id = "synthetic-key",
                private_key = "-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----",
                token_uri = "https://oauth2.googleapis.com/token",
            });
            return Service.ConfigureFreshAsync("synthetic-account", "Synthetic", "synthetic-credential", key.AsMemory(),
                new("https://texttospeech.googleapis.com/v1/voices"), new("https://texttospeech.googleapis.com/v1/text:synthesize"),
                "global", TestContext.Current.CancellationToken);
        }

        public void Clear()
        {
            Bootstrap.Messages.Clear(); Catalog.Messages.Clear(); Probe.Messages.Clear();
            Bootstrap.States.Clear(); Catalog.States.Clear(); Probe.States.Clear();
        }
        public void AssertPrivateValuesAbsent()
        {
            foreach (string message in Bootstrap.Messages.Concat(Catalog.Messages).Concat(Probe.Messages))
                foreach (string forbidden in new[] { "private-key", "bearer", "article-content", "private-storage-detail", "private-response-body", "synthetic-token", "synthetic-project", "synthetic-account", "synthetic-credential" })
                    Assert.DoesNotContain(forbidden, message, StringComparison.Ordinal);
            Assert.All(Bootstrap.Exceptions.Concat(Catalog.Exceptions).Concat(Probe.Exceptions), Assert.Null);
            string[] allowed = ["RefreshId", "CatalogPhase", "CatalogErrorCategory", "OAuthFailure", "HttpStatus", "StorageCode", "StorageExtendedCode", "{OriginalFormat}"];
            foreach (var fields in Bootstrap.States.Concat(Catalog.States).Concat(Probe.States))
                foreach (var field in fields)
                {
                    Assert.Contains(field.Key, allowed, StringComparer.Ordinal);
                    string rendered = Convert.ToString(field.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    foreach (string forbidden in new[] { "private-", "synthetic-token", "synthetic-project", "synthetic-account", "synthetic-credential" })
                        Assert.DoesNotContain(forbidden, rendered, StringComparison.Ordinal);
                }
        }
        public void Dispose() => _client.Dispose();
    }

    private interface ICaptureLogger
    {
        void ThrowDuringLogging(bool failIsEnabled);
    }

    private sealed class CaptureLogger<T> : ILogger<T>, ICaptureLogger
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public List<KeyValuePair<string, object?>[]> States { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        private bool _failLog, _failEnabled;
        public void ThrowDuringLogging(bool failIsEnabled) { _failEnabled = failIsEnabled; _failLog = !failIsEnabled; }
        public bool IsEnabled(LogLevel logLevel) => _failEnabled ? throw new InvalidOperationException("private-logging-failure") : true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (_failLog) throw new InvalidOperationException("private-logging-failure"); Messages.Add(formatter(state, exception)); Exceptions.Add(exception); if (state is IEnumerable<KeyValuePair<string, object?>> fields) States.Add(fields.ToArray()); }
    }
}
