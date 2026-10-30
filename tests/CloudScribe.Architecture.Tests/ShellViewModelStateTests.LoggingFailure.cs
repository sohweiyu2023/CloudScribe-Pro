using CloudScribe.App.ViewModels;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage6LoggerFailureCannotSkipPrivateAttachClaimOrReplaceProviderOutcome(bool failEnabled)
    {
        using var model = CreateViewModel(new ThrowingShellObserver(failEnabled));
        var request = new GenerationProviderRequest("google-cloud-text-to-speech", "synthesize-speech", "synthetic-account", "synthetic-log-request", "synthetic"u8.ToArray(), "MP3");
        var clean = new GoogleGenerationPersistedQueueState(request.AccountId, request.OperationStableId, request.IdempotencyKey, false, null);
        GoogleGenerationPersistedQueueState stored = clean;
        int attached = 0, calls = 0, claims = 0;
        var queue = new GoogleGenerationQueueCoordinator((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GenerationProviderResponse(SubmissionDisposition.RejectedSafeToRetry, null,
                ReadOnlyMemory<byte>.Empty, null, null, "google-http-400", GoogleErrorStatus.InvalidArgument));
        });
        var bound = new GoogleGenerationBoundQueueCoordinator(queue, (_, _, _, _) => Task.FromResult<GoogleGenerationPersistedQueueState?>(stored),
            (value, _) => { stored = value; return Task.CompletedTask; },
            (value, _) => { claims++; stored = value; return Task.FromResult(true); },
            (value, _) => { stored = value; return Task.CompletedTask; });
        var trust = LoggingTrust();
        var snapshot = new GoogleGenerationUiExecutionSnapshot(new(request.AccountId, "synthetic-project", "voice-test", "synthetic-model", "synthetic-capability", "MP3"),
            true, true, true, true, request, trust, clean, clean, GoogleGenerationReconciliationResolutionEvidence.None, true, true, true, true);
        model.ConfigureStage6GoogleGeneration(_ => Task.FromResult(new GoogleGenerationUiExecutionContext(new(bound), snapshot)));
        model.ConfigurePrivateGoogleDiagnostics((actual, _) => { Assert.Same(request, actual); attached++; }, () => { });
        ConfigureGoogleSpendReview(model);
        await model.PrepareGoogleGenerationSpendReviewAsync(TestContext.Current.CancellationToken);
        await model.ApproveGoogleGenerationSpendAsync(50, true, TestContext.Current.CancellationToken);
        await model.GenerateWithGoogleCommand.ExecuteAsync(null);
        Assert.Equal(1, attached); Assert.Equal(1, claims); Assert.Equal(1, calls);
        Assert.True(stored.SubmissionAttempted); Assert.False(stored.UnresolvedSubmission);
        Assert.Equal("google-http-400", stored.DiagnosticCode);
        Assert.False(model.PreparedGoogleGenerationSpendApproved);
        Assert.Null(model.PreparedGoogleGenerationSpendReview);
        Assert.Contains("google-http-400", model.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_LOGGER", model.StatusMessage, StringComparison.Ordinal);
    }

    private static GenerationCacheTrustContext LoggingTrust() => new("google-cloud-text-to-speech", "synthetic-account", "synthetic-project",
        "synthetic-endpoint", "global", "synthesize-speech", "synthetic-model", "voice-test", "synthetic-fingerprint", "synthetic-plan",
        "en-US", "synthetic-controls", "MP3", "synthetic-sample", "synthetic-adapter", "synthetic-compiler", "synthetic-ast",
        "synthetic-normalization", "pricing-test", "synthetic-capability", "synthetic-governance", "synthetic-feature", "synthetic-account-capability");

    private sealed class ThrowingShellObserver(bool failEnabled) : ILogger<ShellViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => failEnabled ? throw new InvalidOperationException("SYNTHETIC_PRIVATE_LOGGER") : true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("SYNTHETIC_PRIVATE_LOGGER");
    }
}
