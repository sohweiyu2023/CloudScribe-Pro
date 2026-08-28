namespace CloudScribe.Domain.Observability;

public readonly record struct ExactMoney
{
    public ExactMoney(long units, int scale, string currencyCode)
    {
        if (scale is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);
        string normalizedCurrencyCode = currencyCode.Trim().ToUpperInvariant();
        if (normalizedCurrencyCode.Length != 3 || normalizedCurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency code must be a three-letter ISO-style token.", nameof(currencyCode));
        }

        Units = units;
        Scale = scale;
        CurrencyCode = normalizedCurrencyCode;
    }

    public long Units { get; }

    public int Scale { get; }

    public string CurrencyCode { get; }

    public bool IsValid =>
        Scale is >= 0 and <= 9
        && CurrencyCode is { Length: 3 }
        && CurrencyCode.All(static character => character is >= 'A' and <= 'Z');

    public void EnsureValid(string? parameterName = null)
    {
        if (!IsValid)
        {
            throw new ArgumentException(
                "Exact money is uninitialized or contains an invalid scale or currency code.",
                parameterName);
        }
    }

    public decimal ToDecimal()
    {
        EnsureValid();
        return Units / Pow10(Scale);
    }

    private static decimal Pow10(int scale)
    {
        decimal result = 1m;
        for (int index = 0; index < scale; index++)
        {
            result *= 10m;
        }

        return result;
    }
}
