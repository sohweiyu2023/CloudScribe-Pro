using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed class GoogleOAuthDiagnosticsTests
{
    [Theory]
    [InlineData("invalid_grant", GoogleOAuthFailure.InvalidGrant)]
    [InlineData("invalid_client", GoogleOAuthFailure.InvalidClient)]
    [InlineData("unauthorized_client", GoogleOAuthFailure.UnauthorizedClient)]
    [InlineData("invalid_scope", GoogleOAuthFailure.InvalidScope)]
    [InlineData("invalid_request", GoogleOAuthFailure.InvalidRequest)]
    [InlineData("access_denied", GoogleOAuthFailure.AccessDenied)]
    [InlineData("temporarily_unavailable", GoogleOAuthFailure.TemporarilyUnavailable)]
    [InlineData("server_error", GoogleOAuthFailure.ServerError)]
    [InlineData("private-token-echo", GoogleOAuthFailure.Unrecognized)]
    public Task OAuthRejectionRetainsOnlyAllowlistedReason(string reason, GoogleOAuthFailure expected) =>
        CheckAsync(JsonSerializer.Serialize(new { error = reason, error_description = "private-description-and-article" }), expected);

    [Fact]
    public Task MalformedOAuthBodyDoesNotReplaceTheHttpFailure() => CheckAsync("{private-description", GoogleOAuthFailure.Malformed);

    [Fact]
    public Task OversizedOAuthBodyIsBounded() => CheckAsync(new string('x', 9000), GoogleOAuthFailure.TooLarge);

    [Fact]
    public Task DuplicateErrorFieldsAreNotReportedAsAnAuthoritativeCause() =>
        CheckAsync("{\"error\":\"invalid_grant\",\"error\":\"invalid_client\"}", GoogleOAuthFailure.Malformed);

    [Fact]
    public Task HeadersThenStalledBodyKeepsOriginalHttpFailureWithinShortDeadline() =>
        CheckAsync(string.Empty, GoogleOAuthFailure.TimedOut, stalled: true);

    private static async Task CheckAsync(string body, GoogleOAuthFailure expected, bool stalled = false)
    {
        var vault = new MemoryVault();
        using var key = RSA.Create(2048);
        string json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            project_id = "synthetic-project",
            client_email = "synthetic@synthetic-project.iam.gserviceaccount.com",
            private_key_id = "synthetic-key-id",
            private_key = key.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.googleapis.com/token",
        });
        var token = TestContext.Current.CancellationToken;
        await new GoogleServiceAccountCredentialOnboardingService(vault).ImportAsync("synthetic-credential", json.AsMemory(), token).ConfigureAwait(false);
        using var handler = new Reject(body, stalled);
        using var client = new HttpClient(handler);
        var logger = new Capture();
        using var resolver = new VaultBackedTransientCredentialResolver(vault, client, TimeProvider.System, logger);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAccessTokenAsync("synthetic-credential", token).AsTask()).ConfigureAwait(false);

        if (stalled) Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(400, GoogleCatalogRefreshDiagnostics.HttpStatus(error));
        Assert.Equal(GoogleCatalogRefreshPhase.OAuthTokenRequest, GoogleCatalogRefreshDiagnostics.ReportedPhase(error, null));
        Assert.Equal(expected, GoogleCatalogRefreshDiagnostics.OAuthFailure(error));
        Assert.Contains(logger.Messages, line => line.Contains("HTTP 400", StringComparison.Ordinal));
        foreach (string line in logger.Messages.Append(error.Message))
            foreach (string secret in new[] { "private-token-echo", "private-description", "synthetic-credential", "synthetic-project", "synthetic@synthetic-project.iam.gserviceaccount.com", "BEGIN PRIVATE KEY" })
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
    }

    private sealed class Reject(string body, bool stalled) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = stalled ? new StreamContent(new StalledStream()) : new StringContent(body) }); }
    }

    private sealed class StalledStream() : MemoryStream(new byte[1])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class MemoryVault : ICredentialVault
    {
        private readonly Dictionary<string, char[]> _items = new(StringComparer.Ordinal);
        public ValueTask StoreAsync(CredentialReference reference, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default)
        { _items[reference.TargetName] = secret.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask<CredentialSecret?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.TryGetValue(reference.TargetName, out var secret) ? new CredentialSecret(secret.ToArray()) : null);
        public ValueTask<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) => ValueTask.FromResult(_items.Remove(reference.TargetName));
    }

    private sealed class Capture : ILogger<VaultBackedTransientCredentialResolver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
}
