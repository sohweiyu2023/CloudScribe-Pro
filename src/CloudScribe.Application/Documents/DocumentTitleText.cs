using System.Globalization;

namespace CloudScribe.Application.Documents;

/// <summary>Fits a suggested document title into the library's title limit without splitting a character: the cut
/// is made on a text-element boundary, so a surrogate pair (for example a CJK Extension B character or an emoji) or a
/// combining sequence is never broken into an invalid or different character.</summary>
public static class DocumentTitleText
{
    /// <summary>The document library's maximum title length in UTF-16 code units.</summary>
    public const int LibraryMaximum = 240;

    public static string Fit(string title, int maximumCodeUnits)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCodeUnits, 1);
        string trimmed = title.Trim();
        if (trimmed.Length <= maximumCodeUnits) return trimmed;
        int end = 0;
        var elements = StringInfo.GetTextElementEnumerator(trimmed);
        while (elements.MoveNext())
        {
            int next = elements.ElementIndex + ((string)elements.Current).Length;
            if (next > maximumCodeUnits) break;
            end = next;
        }
        string fitted = trimmed[..end].TrimEnd();
        // A single text element longer than the limit is kept whole rather than broken.
        return fitted.Length > 0 ? fitted : StringInfo.GetNextTextElement(trimmed);
    }
}
