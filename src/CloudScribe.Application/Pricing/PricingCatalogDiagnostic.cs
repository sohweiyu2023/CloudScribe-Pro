namespace CloudScribe.Application.Pricing;

public sealed record PricingCatalogDiagnostic
{
    public PricingCatalogDiagnostic(string code, string message, string? jsonPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code.Trim();
        Message = message.Trim();
        JsonPath = string.IsNullOrWhiteSpace(jsonPath) ? null : jsonPath.Trim();
    }

    public string Code { get; }
    public string Message { get; }
    public string? JsonPath { get; }
}
