using System.Net;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Theory]
    [InlineData("disable")]
    [InlineData("unchecked")]
    [InlineData("closed")]
    [InlineData("request-changed")]
    public async Task PendingOptInCannotRearmAfterInvalidation(string reason)
    {
        using var f = new Fixture(); long review = f.Store.BeginOptInReview();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> DelayedReviewAsync() { await ready.Task.ConfigureAwait(false); return f.Store.CompleteOptInReview(review, f.Binding, true); }
        var pending = DelayedReviewAsync();
        f.Store.Disarm(); ready.SetResult();
        Assert.False(await pending); Assert.False(f.Store.IsArmed); Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public void ConsentIsRequiredAtCompletionAndNoSubmissionOccursWhileArming()
    {
        using var f = new Fixture(); long review = f.Store.BeginOptInReview();
        Assert.False(f.Store.CompleteOptInReview(review, f.Binding, false)); Assert.False(f.Store.IsArmed);
        Assert.Throws<InvalidOperationException>(() => f.Store.Arm(f.Binding, false)); Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task BlockedPrivateWriterNeverBlocksKnownProviderOutcome()
    {
        using var writer = new BlockingWriter(); using var f = new Fixture(writer); f.Arm();
        f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "Unfamiliar limit explained privately." } });
        try
        {
            var response = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("google-http-400", response.DiagnosticCode); Assert.False(response.IsAccepted);
            Assert.True(writer.Started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)); Assert.False(f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken).IsCompleted);
            f.Store.Disarm(); Assert.False(f.Store.IsArmed); Assert.Equal(1, f.Handler.Calls);
        }
        finally { writer.Release.Set(); await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task PrivateWriteFailureDoesNotChangeResponseOrPermitAnotherCapture()
    {
        using var f = new Fixture(new BrokenWriter()); f.Arm();
        var result = await f.SendAsync("A new validation reason.");
        Assert.Equal("google-http-400", result.DiagnosticCode); Assert.Equal(HttpStatusCode.BadRequest, f.Handler.Status);
        Assert.False(f.Store.IsArmed); Assert.Equal(1, f.Handler.Calls); Assert.Empty(f.Store.ReadRecords());
    }

    private sealed class BlockingWriter : IPrivateProviderErrorPersistence, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Save(PrivateProviderErrorRecord record) { Started.Set(); Release.Wait(); return true; }
        public IReadOnlyList<PrivateProviderErrorRecord> Read() => [];
        public void Dispose() { Release.Set(); Started.Dispose(); Release.Dispose(); }
    }
    private sealed class BrokenWriter : IPrivateProviderErrorPersistence
    {
        public bool Save(PrivateProviderErrorRecord record) => throw new IOException("SYNTHETIC PRIVATE exception text must not reach logs/outcome.");
        public IReadOnlyList<PrivateProviderErrorRecord> Read() => [];
    }
}
