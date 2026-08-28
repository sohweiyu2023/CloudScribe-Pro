using System.Text;

namespace CloudScribe.Infrastructure.Files;

internal static class BoundedTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, true, true);

    public static string Decode(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return StrictUtf8.GetString(source, 3, source.Length - 3);
        }

        if (source.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return StrictUtf16LittleEndian.GetString(source, 2, source.Length - 2);
        }

        if (source.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return StrictUtf16BigEndian.GetString(source, 2, source.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(source);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Text import is not valid UTF-8 and has no supported Unicode BOM.",
                exception);
        }
    }
}
