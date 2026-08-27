using CloudScribe.Application.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage7VoiceLabCatalogSingleFlightTests
{
    [Fact]
    public async Task Concurrent_catalog_query_is_rejected_before_second_transport_call()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCalls = 0;
        var service = new VoiceLabCatalogQueryService(async (_, _) =>
        {
            Interlocked.Increment(ref transportCalls);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return Array.Empty<CloudScribe.Domain.Generation.VoiceLabCatalogSelection>();
        });
        var query = new VoiceLabCatalogQuery("provider", "acct", "project", null, null, false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var first = service.QueryAsync(query, true, true, false, cancellationToken);
        await entered.Task.ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.QueryAsync(query, true, true, false, cancellationToken)).ConfigureAwait(true);
        Assert.Equal(1, Volatile.Read(ref transportCalls));

        release.TrySetResult();
        await first.ConfigureAwait(true);
        Assert.Equal(1, transportCalls);
    }
}
