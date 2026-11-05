namespace CloudScribe.Infrastructure.Generation;

/// <summary>PRIVATE, redacted provider text. Never send to ILogger, normal exports, telemetry or source packages.</summary>
public sealed record PrivateProviderErrorRecord(Guid Id, DateTimeOffset CreatedUtc, string Message, bool Truncated,
    string PayloadSha256 = "", string TransportCorrelation = "",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? ProviderDetailsOmitted = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PrivateProviderResponseEnvelope? Response = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ArticleJobId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? ArticleOrdinal = null)
{
    public override string ToString() => "Private provider error record (contents omitted)";
}
