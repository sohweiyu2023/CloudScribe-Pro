using System.Collections;
using CloudScribe.Application.Logging;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Application.Tests;

public sealed class LocalRequestDiagnosticsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-02T03:04:05Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(LocalRequestReason.ReservedRequestIneligible)]
    [InlineData(LocalRequestReason.ReservedIdentityMalformed)]
    [InlineData(LocalRequestReason.ReservedSourceChanged)]
    [InlineData(LocalRequestReason.ReservedEnvelopeChanged)]
    [InlineData(LocalRequestReason.ReservedPayloadChanged)]
    [InlineData(LocalRequestReason.ReservedRevisionChanged)]
    [InlineData(LocalRequestReason.CaptureCapabilityBindingChanged)]
    public void ReservedCaptureFailuresRetainExactInvariantWithoutExceptionContent(LocalRequestReason reason)
    {
        const string privateText = "Bearer synthetic-secret private article fragment";
        var logger = new CaptureLogger();
        var error = LocalRequestDiagnostics.Mark(new InvalidOperationException(privateText), reason,
            admissionExpiresUtc: Now.AddMinutes(5), capabilityExpiresUtc: Now.AddSeconds(-1));
        var report = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), LocalRequestStage.CaptureArm, error, Now);
        Assert.Equal(reason, report.Reason); Assert.True(report.EvidenceComplete);
        Assert.Equal(Now.AddSeconds(-1), report.CapabilityExpiresUtc);
        Assert.Equal(Now.AddMinutes(5), report.AdmissionExpiresUtc);
        Assert.DoesNotContain(privateText, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, LocalRequestDiagnostics.UserMessage(report), StringComparison.Ordinal);
        Assert.Null(logger.Exception);
    }

    [Fact]
    public void ExpiredReviewRetainsSafeReasonAndSeparateExpiryWithoutChangingExceptionType()
    {
        var original = new InvalidOperationException("synthetic-secret article fragment bearer private-token");
        var marked = LocalRequestDiagnostics.Mark(original, LocalRequestReason.ReviewExpired, Now.AddMinutes(-11), Now.AddHours(1));
        Assert.Same(original, marked); Assert.IsType<InvalidOperationException>(marked);
        var logger = new CaptureLogger();
        var report = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), LocalRequestStage.Reservation,
            new UnfamiliarException("wrapped synthetic-secret", marked), Now);
        Assert.Equal(LocalRequestReason.ReviewExpired, report.Reason);
        Assert.Equal(660_000, report.ReviewAgeMilliseconds);
        Assert.Equal(Now.AddMinutes(-1), report.ReviewExpiresUtc);
        Assert.Equal(Now.AddHours(1), report.AdmissionExpiresUtc);
        Assert.True(report.EvidenceComplete);
        Assert.Contains("10 minutes", LocalRequestDiagnostics.UserMessage(report), StringComparison.Ordinal);
        Assert.Contains("Reservation", logger.Text, StringComparison.Ordinal);
        Assert.Contains("ReviewExpired", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", logger.Text, StringComparison.Ordinal);
        Assert.Null(logger.Exception);
        Assert.Equal(1, logger.Fields.Single(x => string.Equals(x.Key, "SchemaVersion", StringComparison.Ordinal)).Value);
        Assert.Equal(LocalRequestStage.Reservation, logger.Fields.Single(x => string.Equals(x.Key, "Stage", StringComparison.Ordinal)).Value);
        Assert.Equal(LocalRequestReason.ReviewExpired, logger.Fields.Single(x => string.Equals(x.Key, "Reason", StringComparison.Ordinal)).Value);
        Assert.Equal(660_000L, logger.Fields.Single(x => string.Equals(x.Key, "ReviewAgeMilliseconds", StringComparison.Ordinal)).Value);
        Assert.Equal(Now.AddMinutes(-1), logger.Fields.Single(x => string.Equals(x.Key, "ReviewExpiresUtc", StringComparison.Ordinal)).Value);
        Assert.Equal(Now.AddHours(1), logger.Fields.Single(x => string.Equals(x.Key, "AdmissionExpiresUtc", StringComparison.Ordinal)).Value);
        Assert.Equal(true, logger.Fields.Single(x => string.Equals(x.Key, "EvidenceComplete", StringComparison.Ordinal)).Value);
    }

    [Theory]
    [InlineData(LocalRequestStage.ArticleOpen)]
    [InlineData(LocalRequestStage.CoverageRead)]
    [InlineData(LocalRequestStage.Review)]
    [InlineData(LocalRequestStage.Reservation)]
    [InlineData(LocalRequestStage.CaptureOpen)]
    [InlineData(LocalRequestStage.CaptureArm)]
    [InlineData(LocalRequestStage.CaptureRead)]
    [InlineData(LocalRequestStage.OutputRepair)]
    [InlineData(LocalRequestStage.Export)]
    [InlineData(LocalRequestStage.ConnectionRead)]
    [InlineData(LocalRequestStage.PreferenceWrite)]
    [InlineData(LocalRequestStage.PricingActivation)]
    [InlineData(LocalRequestStage.GenerationDispatch)]
    public void UnfamiliarExceptionsRetainStageAndEvidenceWarningWithoutEchoingAnything(LocalRequestStage stage)
    {
        const string sensitive = "-----BEGIN PRIVATE KEY----- synthetic article C:\\Users\\private\\document.txt token=secret";
        var logger = new CaptureLogger();
        var report = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), stage, new UnfamiliarException(sensitive), Now, Now.AddMinutes(2));
        Assert.Equal(LocalRequestReason.Unexpected, report.Reason); Assert.False(report.EvidenceComplete);
        Assert.Equal(-1, report.ReviewAgeMilliseconds); Assert.Null(report.ReviewExpiresUtc);
        Assert.Equal(Now.AddMinutes(2), report.AdmissionExpiresUtc);
        Assert.DoesNotContain(sensitive, logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("document.txt", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, LocalRequestDiagnostics.UserMessage(report), StringComparison.Ordinal);
        Assert.Null(logger.Exception);
        Assert.Equal(false, logger.Fields.Single(x => string.Equals(x.Key, "EvidenceComplete", StringComparison.Ordinal)).Value);
        Assert.DoesNotContain(logger.Fields, x => x.Value is Exception || (x.Value?.ToString()?.Contains("token=", StringComparison.Ordinal) ?? false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingLoggerCannotReplaceOriginalFailureOrItsSafeDescription(bool throwIsEnabled)
    {
        var logger = new CaptureLogger { ThrowIsEnabled = throwIsEnabled, ThrowLog = !throwIsEnabled };
        long before = CloudScribeLog.ObserverFailureCount;
        var report = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), LocalRequestStage.Reservation,
            new IOException("private-path and article"), Now);
        Assert.Equal(LocalRequestReason.Storage, report.Reason);
        Assert.True(CloudScribeLog.ObserverFailureCount > before);
    }

    [Fact]
    public void DisabledLoggerDoesNotDiscardTheSafeFailureOrEvaluateTheSink()
    {
        var logger = new CaptureLogger { Enabled = false, ThrowLog = true };
        var report = LocalRequestDiagnostics.Failure(logger, Guid.NewGuid(), LocalRequestStage.DocumentSave,
            new IOException("synthetic private editor fragment"), Now);
        Assert.Equal(LocalRequestReason.Storage, report.Reason);
        Assert.Empty(logger.Text);
        Assert.Empty(logger.Fields);
    }

    [Fact]
    public void HostileMetadataAndUnknownEnumsCannotInjectTextOrBreakFailureReporting()
    {
        var error = new HostileDataException();
        Assert.Same(error, LocalRequestDiagnostics.Mark(error, LocalRequestReason.ReviewExpired));
        var logger = new CaptureLogger();
        var report = LocalRequestDiagnostics.Failure(logger, Guid.Empty, (LocalRequestStage)int.MaxValue, error, Now);
        Assert.Equal(LocalRequestReason.Unexpected, report.Reason); Assert.Contains("Unknown", logger.Text, StringComparison.Ordinal);
        var invalid = LocalRequestDiagnostics.Mark(new UnfamiliarException("secret"), (LocalRequestReason)int.MaxValue);
        Assert.Equal(LocalRequestReason.Unexpected, LocalRequestDiagnostics.Describe(invalid, Now).Reason);
    }

    [Fact]
    public void MissingClockEvidenceIsExplicitAndUnmarkedValidationNeverInventsAnExpiryCause()
    {
        var report = LocalRequestDiagnostics.Describe(new InvalidOperationException("review might be expired"), Now);
        Assert.Equal(LocalRequestReason.ValidationRejected, report.Reason); Assert.False(report.EvidenceComplete);
        Assert.Equal(-1, report.ReviewAgeMilliseconds); Assert.Null(report.ReviewExpiresUtc);
        var backward = LocalRequestDiagnostics.Mark(new UnfamiliarException(), LocalRequestReason.ReviewExpired, Now.AddMinutes(1));
        Assert.Equal(0, LocalRequestDiagnostics.Describe(backward, Now).ReviewAgeMilliseconds);
    }

    private sealed class UnfamiliarException(string? message = null, Exception? inner = null) : Exception(message, inner);

    private sealed class HostileDataException : Exception
    {
        public override IDictionary Data => throw new IOException("synthetic sensitive metadata failure");
    }

    private sealed class CaptureLogger : ILogger
    {
        public string Text { get; private set; } = string.Empty;
        public Exception? Exception { get; private set; }
        public List<KeyValuePair<string, object?>> Fields { get; } = [];
        public bool ThrowIsEnabled { get; init; }
        public bool ThrowLog { get; init; }
        public bool Enabled { get; init; } = true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => ThrowIsEnabled ? throw new IOException("synthetic logger failure") : Enabled;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (ThrowLog) throw new IOException("synthetic sink failure");
            Text += formatter(state, exception); Exception = exception;
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) Fields.AddRange(fields);
        }
    }
}
