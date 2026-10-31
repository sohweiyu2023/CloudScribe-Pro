using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using CloudScribe.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SafeExceptionSummarizerTests
{
    private const string Private = "SYNTHETIC_PRIVATE_ARTICLE_AND_TOKEN_SENTINEL";

    [Fact]
    public void HostileExceptionPropertiesAreNeverReadAndPrivateTypeNamesAreNotEncoded()
    {
        var error = new SyntheticPrivateExceptionTypeMarker();
        var summary = SafeExceptionSummarizer.Capture(error)!;
        string json = JsonSerializer.Serialize(summary);
        Assert.Equal(SafeExceptionCategory.Unknown, summary.Category);
        Assert.Equal(0, error.Accesses);
        Assert.DoesNotContain(Private, json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(SyntheticPrivateExceptionTypeMarker), json, StringComparison.Ordinal);
        Assert.DoesNotContain("Message", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrownStorageErrorRetainsCodeLocationIdentityWithoutPathsOrText()
    {
        IOException error;
        try { ThrowStorageFailure(); throw new InvalidOperationException("Unreachable."); }
        catch (IOException captured) { error = captured; }
        var summary = SafeExceptionSummarizer.Capture(error)!;
        Assert.Equal(SafeExceptionCategory.Storage, summary.Category);
        Assert.Equal(error.HResult, summary.ErrorCode);
        Assert.Equal(SafeExceptionStackState.Available, summary.StackState);
        Assert.NotEmpty(summary.Locations);
        Assert.True(summary.Locations.Count <= SafeExceptionSummarizer.MaximumLocations);
        Assert.All(summary.Locations, location =>
        {
            Assert.NotEqual(Guid.Empty, location.ModuleId);
            Assert.Equal(0x06000000, location.MethodToken & unchecked((int)0xff000000));
            Assert.True(location.IlOffset >= -1);
        });
        string json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain(Private, json, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ThrowStorageFailure), json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepAndAggregateCausesAreBoundedAndExplicitlyTruncated()
    {
        Exception error = new IOException(Private);
        for (int index = 0; index < 20; index++) error = new InvalidOperationException(Private, error);
        var summary = SafeExceptionSummarizer.Capture(error)!;
        Assert.Equal(SafeExceptionSummarizer.MaximumCauses - 1, summary.InnerCauses.Count);
        Assert.True(summary.Truncated);
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        var aggregate = new AggregateException(Enumerable.Range(0, 256).Select(_ => new IOException(Private)));
        var aggregateSummary = SafeExceptionSummarizer.Capture(aggregate)!;
        Assert.True(aggregateSummary.Truncated);
        Assert.True(aggregateSummary.InnerCauses.Count < SafeExceptionSummarizer.MaximumCauses);
    }

    [Fact]
    public void NativeAndDatabaseNumericCodesAreRetainedWithoutTheirDescriptions()
    {
        var native = SafeExceptionSummarizer.Capture(new Win32Exception(5, Private))!;
        Assert.Equal(SafeExceptionCategory.NativeInterop, native.Category);
        Assert.Equal(5, native.NativeErrorCode);
        var database = SafeExceptionSummarizer.Capture(new SqliteException(Private, 13))!;
        Assert.Equal(SafeExceptionCategory.Database, database.Category);
        Assert.Equal(13, database.SqlitePrimaryCode);
        Assert.Equal(13, database.SqliteExtendedCode);
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(native), StringComparison.Ordinal);
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(database), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingStackAndNullAreExplicitRatherThanInventingAnErrorExplanation()
    {
        Assert.Null(SafeExceptionSummarizer.Capture(null));
        Assert.Null(SafeExceptionSummarizer.Describe(null));
        var summary = SafeExceptionSummarizer.Capture(new InvalidOperationException(Private))!;
        Assert.Equal(1, summary.SchemaVersion);
        Assert.Equal(SafeExceptionStackState.Missing, summary.StackState);
        Assert.Empty(summary.Locations);
        Assert.Empty(summary.InnerCauses);
    }

    private static void ThrowStorageFailure() => throw new IOException(Private);

    private sealed class SyntheticPrivateExceptionTypeMarker : Exception
    {
        public int Accesses { get; private set; }
        public override string Message { get { Accesses++; throw new IOException(Private); } }
        public override string? StackTrace { get { Accesses++; throw new IOException(Private); } }
        public override IDictionary Data { get { Accesses++; throw new IOException(Private); } }
        public override string ToString() { Accesses++; throw new IOException(Private); }
    }
}
