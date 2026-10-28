using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Generation;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    [Fact]
    public async Task RealDurableQueueOutcomeCanSaveWhilePrivateStorageIsBlocked()
    {
        using var writer = new BlockingWriter(); using var f = new Fixture(writer);
        Directory.CreateDirectory(f.Root);
        var factory = new PrivateDiagnosticDbFactory(f.Root);
        using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var queue = new GoogleGenerationPersistedQueueStateStore(factory);
        var initial = new GoogleGenerationPersistedQueueState(f.Request.AccountId, f.Request.OperationStableId, f.Request.IdempotencyKey, false, null);
        await queue.SaveAsync(initial, TestContext.Current.CancellationToken);
        var claim = initial with
        {
            SubmissionAttempted = true,
            UnresolvedSubmission = true,
            LocalAttemptId = Guid.NewGuid().ToString("N"),
            DiagnosticCode = "google-dispatch-claimed",
            PayloadSha256 = f.Binding.PayloadSha256,
            PayloadBytes = f.Request.CompiledPayload.Length,
            OutcomeObservedAtUnixMilliseconds = 1
        };
        Assert.True(await queue.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        f.Arm(); f.Handler.Body = JsonSerializer.Serialize(new { error = new { status = "INVALID_ARGUMENT", message = "Novel synthetic durable failure explanation." } });
        try
        {
            var response = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var outcome = claim with { UnresolvedSubmission = false, DiagnosticCode = response.DiagnosticCode, ErrorStatus = response.ErrorStatus, OutcomeObservedAtUnixMilliseconds = 2 };
            await queue.SaveOutcomeAsync(outcome, TestContext.Current.CancellationToken);
            var restarted = await new GoogleGenerationPersistedQueueStateStore(factory).LoadAsync(initial.AccountId, initial.OperationStableId, initial.IdempotencyKey, TestContext.Current.CancellationToken);
            Assert.Equal(outcome, restarted); Assert.False(f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken).IsCompleted);
            Assert.Equal(1, f.Handler.Calls); Assert.False(await queue.TryClaimSubmissionAsync(claim, TestContext.Current.CancellationToken));
        }
        finally { writer.Release.Set(); await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task PrivateUnknownMessageNeverEntersNormalLoggerOrRecordToString()
    {
        var logger = new PrivateLogger(); using var f = new Fixture(logger: logger); f.Arm();
        const string diagnostic = "UNFAMILIAR_PROVIDER_WORDING_ONLY_FOR_PRIVATE_VIEW";
        await f.SendAsync(diagnostic);
        var record = Assert.Single(f.Store.ReadRecords()); Assert.Equal(diagnostic, record.Message);
        string log = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(diagnostic, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC PRIVATE INPUT", log, StringComparison.Ordinal);
        Assert.NotEmpty(logger.FieldValues);
        string structured = JsonSerializer.Serialize(logger.FieldValues);
        Assert.DoesNotContain(diagnostic, structured, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", structured, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC PRIVATE INPUT", structured, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(f.Request.CompiledPayload.Span), structured, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(f.Handler.Body)), structured, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, record.ToString(), StringComparison.Ordinal); Assert.False(logger.SawException);
    }

    private sealed class PrivateDiagnosticDbFactory(string root) : IDbContextFactory<CloudScribeDbContext>
    {
        public CloudScribeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "synthetic-queue.db"), Pooling = false }.ConnectionString).Options);
    }
    private sealed class PrivateLogger : ILogger<GoogleGenerationHttpTransport>
    {
        public List<string> Messages { get; } = [];
        public List<object?> FieldValues { get; } = [];
        public bool SawException { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception)); SawException |= exception is not null;
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) FieldValues.AddRange(fields.Select(x => x.Value));
        }
    }
}
