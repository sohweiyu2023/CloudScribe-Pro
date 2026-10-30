using CloudScribe.App.Composition;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public async Task CredentialRepairLoggingFactoryFailureCannotReplaceVerificationOutcome()
    {
        using var loggerFactory = new RepairThrowingLoggerFactory();
        await using var f = await RepairFixture.CreateAsync(loggerFactory);
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        Assert.NotNull(review);
        Assert.Equal(3, f.Handler.Requests);
        Assert.Equal(0, f.Vault.Writes);
        service.Discard(review.ReviewId);
    }

    [Fact]
    public async Task CredentialRepairExpiryAfterAwaitedPresenceStopsFurtherStaging()
    {
        await using var f = await RepairFixture.CreateAsync();
        var service = f.NewService();
        var review = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        f.Vault.OnInspect = name =>
        {
            if (f.Vault.Writes == 1 && name.EndsWith(".google-service-account-metadata", StringComparison.Ordinal))
                f.Clock.Now = review.ExpiresAtUtc;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(review.ReviewId, review.ReviewSha256, TestContext.Current.CancellationToken));
        Assert.Equal(1, f.Vault.Writes);
        Assert.Equal(1, (await f.CurrentAsync()).Revision);
        f.Vault.OnInspect = null;
        var renewed = await service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        Assert.Equal(review.RepairId, renewed.RepairId);
        await service.ConfirmAsync(renewed.ReviewId, renewed.ReviewSha256, TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Vault.Writes);
    }

    [Fact]
    public async Task CredentialRepairServiceShutdownCancelsVerificationWithoutPublishingSecretReview()
    {
        await using var f = await RepairFixture.CreateAsync();
        var service = f.NewService();
        f.Handler.WaitBeforeResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = service.PrepareAsync(RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken);
        await f.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(1, f.Handler.Requests);
        Assert.Equal(0, f.Vault.Writes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.PrepareAsync(
            RepairFixture.Account, RepairFixture.Email, f.Json.AsMemory(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CredentialRepairCancelDuringWriteWaitPreservesOldAccountAndStopsUnstartedWrite()
    {
        await using var f = await RepairFixture.CreateAsync();
        using var controller = new GoogleCredentialRepairController(f.NewService(), RepairFixture.Account) { IntendedEmail = RepairFixture.Email };
        controller.SelectCandidate(f.Json.AsMemory());
        await controller.VerifyAsync(TestContext.Current.CancellationToken);
        controller.Acknowledged = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Vault.BeforeStore = async _ => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); };
        var confirming = controller.ConfirmAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            controller.Dispose();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirming);
        Assert.Equal(0, f.Vault.Writes);
        Assert.Equal(1, (await f.CurrentAsync()).Revision);
    }
    private sealed class RepairThrowingLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => throw new IOException("Synthetic private logger failure.");
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
