namespace CloudScribe.Application.Batch;

/// <summary>Orders file names the way people read them: "Track 2" before "Track 10", case-insensitively,
/// with full-width digits treated as digits. Ties fall back to ordinal order so the result is deterministic.</summary>
public sealed class BatchNaturalOrder : IComparer<string>
{
    public static BatchNaturalOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                int byNumber = CompareDigits(x.AsSpan(si, i - si), y.AsSpan(sj, j - sj));
                if (byNumber != 0) return byNumber;
                continue;
            }
            int byText = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byText != 0) return byText;
            i++; j++;
        }
        int byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    private static int CompareDigits(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        static ReadOnlySpan<char> TrimZeros(ReadOnlySpan<char> s)
        {
            int k = 0;
            while (k < s.Length - 1 && char.GetNumericValue(s[k]) == 0) k++;
            return s[k..];
        }
        a = TrimZeros(a); b = TrimZeros(b);
        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        for (int k = 0; k < a.Length; k++)
        {
            int d = char.GetNumericValue(a[k]).CompareTo(char.GetNumericValue(b[k]));
            if (d != 0) return d;
        }
        return 0;
    }
}
