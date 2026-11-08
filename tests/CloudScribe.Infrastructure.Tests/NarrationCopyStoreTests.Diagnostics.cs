using CloudScribe.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Fact]
    public async Task PreparationDiagnosticsAreContentFreeAndLoggingCannotChangePublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        var logger = new PreparationLogger();
        var store = new EfNarrationCopyStore(fixture.Factory, new(fixture.Paths), TimeProvider.System, logger);
        var prepared = await PrepareAsync(store, source);
        Assert.Equal(prepared, await PrepareAsync(store, source));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(source.Id, source.CurrentRevisionId!.Value, Hash("other"), Token));
        Assert.Contains(logger.Messages, message => message.Contains("NarrationPrepared", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("NarrationReused", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("PrepareNarration", StringComparison.Ordinal));
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(SourceText, message, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", message, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Paths.RootDirectory, message, StringComparison.Ordinal);
            Assert.DoesNotContain(Hash(SourceText), message, StringComparison.Ordinal);
        });
        logger.Throw = true;
        Assert.Equal(prepared, await PrepareAsync(store, source));
    }

    private sealed class PreparationLogger : ILogger<EfNarrationCopyStore>
    {
        public List<string> Messages { get; } = [];
        public bool Throw { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new IOException("Synthetic diagnostic sink failure.");
            Messages.Add(formatter(state, exception));
        }
    }
}
