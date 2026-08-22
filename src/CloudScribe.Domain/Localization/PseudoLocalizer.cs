using System.Globalization;
using System.Text;

namespace CloudScribe.Domain.Localization;

public static class PseudoLocalizer
{
    private static readonly IReadOnlyDictionary<char, char> Accents = new Dictionary<char, char>
    {
        ['a'] = 'á', ['A'] = 'Á', ['e'] = 'ë', ['E'] = 'Ë', ['i'] = 'ï', ['I'] = 'Ï',
        ['o'] = 'ô', ['O'] = 'Ô', ['u'] = 'ü', ['U'] = 'Ü', ['c'] = 'ç', ['C'] = 'Ç',
        ['n'] = 'ñ', ['N'] = 'Ñ', ['y'] = 'ý', ['Y'] = 'Ý',
    };

    public static string Transform(string value, double expansionRatio = 0.35)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (double.IsNaN(expansionRatio) || double.IsInfinity(expansionRatio) || expansionRatio is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(expansionRatio));
        if (value.Length == 0) return value;

        var output = new StringBuilder(value.Length + 16);
        output.Append("[!! ");
        var literalLetters = 0;
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '{')
            {
                var placeholderEnd = FindPlaceholderEnd(value, index);
                output.Append(value, index, placeholderEnd - index + 1);
                index = placeholderEnd + 1;
                continue;
            }

            var nextPlaceholder = value.IndexOf('{', index);
            var end = nextPlaceholder < 0 ? value.Length : nextPlaceholder;
            var literal = value[index..end];
            var enumerator = StringInfo.GetTextElementEnumerator(literal);
            while (enumerator.MoveNext())
            {
                var element = (string)enumerator.Current!;
                foreach (var ch in element)
                {
                    if (char.IsLetter(ch)) literalLetters++;
                    output.Append(Accents.TryGetValue(ch, out var mapped) ? mapped : ch);
                }
            }
            index = end;
        }

        var padding = checked((int)Math.Ceiling(literalLetters * expansionRatio));
        if (padding > 0) output.Append('~', padding);
        output.Append(" !!]");
        return output.ToString();
    }

    private static int FindPlaceholderEnd(string value, int start)
    {
        var depth = 0;
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '{') depth++;
            else if (value[index] == '}')
            {
                depth--;
                if (depth == 0) return index;
                if (depth < 0) break;
            }
        }
        throw new FormatException("Pseudo-localization input contains an unterminated format placeholder.");
    }
}
