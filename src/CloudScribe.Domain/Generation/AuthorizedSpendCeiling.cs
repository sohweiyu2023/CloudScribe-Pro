namespace CloudScribe.Domain.Generation;

public readonly record struct AuthorizedSpendCeiling(string CurrencyCode, long Units, int Scale)
{
    public AuthorizedSpendCeiling Validate()
    {
        return Validate(CurrencyCode, Units, Scale);
    }

    private static AuthorizedSpendCeiling Validate(string currencyCode, long units, int scale)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3 ||
            currencyCode.Any(static character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Spend ceiling currency must be a three-letter uppercase code.", nameof(currencyCode));
        }

        if (units < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(units));
        }

        if (scale is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        return new AuthorizedSpendCeiling(currencyCode, units, scale);
    }

    public bool Allows(AuthorizedSpendCeiling actual)
    {
        Validate();
        actual.Validate();
        if (!string.Equals(CurrencyCode, actual.CurrencyCode, StringComparison.Ordinal) || Scale != actual.Scale)
        {
            return false;
        }

        return actual.Units <= Units;
    }
}
