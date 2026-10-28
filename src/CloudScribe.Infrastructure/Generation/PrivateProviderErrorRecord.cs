namespace CloudScribe.Infrastructure.Generation;

/// <summary>PRIVATE, redacted provider text. Never send to ILogger, normal exports, telemetry or source packages.</summary>
public sealed record PrivateProviderErrorRecord(Guid Id, DateTimeOffset CreatedUtc, string Message, bool Truncated,
    string PayloadSha256 = "", string TransportCorrelation = "")
{
    public override string ToString() => "Private provider error record (contents omitted)";
}
