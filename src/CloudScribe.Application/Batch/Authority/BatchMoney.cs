using System.Globalization;

namespace CloudScribe.Application.Batch.Authority;

/// <summary>Formats amounts. Display conversion is approximate and is always labelled as such.</summary>
public static class BatchMoney
{
    public static decimal ToMajor(long minorUnits, int scale) => minorUnits / (decimal)Math.Pow(10, scale);

    public static long ToMinor(decimal major, int scale) => checked((long)Math.Ceiling(major * (decimal)Math.Pow(10, scale)));

    /// <summary>For example "S$12.40 (US$9.54)". The display figure is shown first, then the billing amount.</summary>
    public static string Format(long minorUnits, string currency, int scale, string displayCurrency, decimal displayRate)
    {
        string billing = Symbol(currency) + ToMajor(minorUnits, scale).ToString("0.00", CultureInfo.InvariantCulture);
        if (string.Equals(currency, displayCurrency, StringComparison.Ordinal) || displayRate <= 0) return billing;
        decimal display = Math.Round(ToMajor(minorUnits, scale) * displayRate, 2, MidpointRounding.AwayFromZero);
        return $"about {Symbol(displayCurrency)}{display.ToString("0.00", CultureInfo.InvariantCulture)} ({billing})";
    }

    public static string Format(long minorUnits, BatchAuthorityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Format(minorUnits, profile.Currency, profile.Scale, profile.DisplayCurrency, profile.DisplayRate);
    }

    private static string Symbol(string currency) => currency switch
    {
        "SGD" => "S$",
        "USD" => "US$",
        "EUR" => "€",
        "GBP" => "£",
        _ => currency + " ",
    };
}
