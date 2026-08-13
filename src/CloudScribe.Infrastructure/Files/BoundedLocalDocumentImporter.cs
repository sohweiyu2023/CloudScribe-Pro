using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Files;

public sealed class BoundedLocalDocumentImporter : ILocalDocumentImporter
{
    private const int MaxSourceBytes = 32 * 1024 * 1024;
    private const long MaxArchiveExpandedBytes = 128L * 1024 * 1024;
    private const long MaxArchiveEntryBytes = 32L * 1024 * 1024;
    private const int MaxArchiveEntries = 2048;
    private const double MaxCompressionRatio = 200.0;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, true, true);

    public async Task<LocalDocumentImportResult> ImportAsync(
        LocalDocumentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("The import display name is required.", nameof(request));
        }

        if (request.Content is null)
        {
            throw new ArgumentException("The import content stream is required.", nameof(request));
        }

        if (!request.Content.CanRead)
        {
            throw new ArgumentException("The import source must be readable.", nameof(request));
        }

        if (request.DeclaredLength is < 0 or > MaxSourceBytes)
        {
            throw new InvalidDataException($"Import source exceeds the {MaxSourceBytes:N0}-byte limit.");
        }

        byte[] source = await ReadBoundedAsync(request.Content, MaxSourceBytes, cancellationToken).ConfigureAwait(false);
        string title = BuildSuggestedTitle(request.DisplayName);
        return request.Kind switch
        {
            LocalDocumentImportKind.PlainText => BuildTextResult(title, source, request, "plain-text"),
            LocalDocumentImportKind.Markdown => BuildTextResult(title, source, request, "markdown"),
            LocalDocumentImportKind.Clipboard => BuildTextResult(title, source, request, "clipboard"),
            LocalDocumentImportKind.Html => BuildHtmlResult(title, source, request),
            LocalDocumentImportKind.Docx => BuildDocxResult(title, source, request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported local import kind."),
        };
    }

    private static LocalDocumentImportResult BuildTextResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request,
        string kind)
    {
        string text = DecodeText(source);
        return new(
            title,
            text,
            BuildProvenance(kind, request.DisplayName),
            source.LongLength,
            []);
    }

    private static LocalDocumentImportResult BuildHtmlResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request)
    {
        string html = DecodeText(source);
        string text = ExtractHtmlText(html);
        return new(
            title,
            text,
            BuildProvenance("html-text-only", request.DisplayName),
            source.LongLength,
            ["HTML was imported as inert text; scripts, styles, markup and active content were discarded."]);
    }

    private static LocalDocumentImportResult BuildDocxResult(
        string title,
        byte[] source,
        LocalDocumentImportRequest request)
    {
        using MemoryStream archiveStream = new(source, writable: false);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        ValidateArchive(archive);
        ZipArchiveEntry documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX does not contain word/document.xml.");
        if (documentEntry.Length > MaxArchiveEntryBytes)
        {
            throw new InvalidDataException("DOCX main document part exceeds the bounded entry limit.");
        }

        string text = ExtractDocxText(documentEntry);
        return new(
            title,
            text,
            BuildProvenance("docx-text-only", request.DisplayName),
            source.LongLength,
            ["DOCX was imported as text only; embedded media, macros, external relationships and formatting were not executed or imported."]);
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("DOCX archive contains too many entries.");
        }

        long totalExpandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > MaxArchiveEntryBytes)
            {
                throw new InvalidDataException("DOCX archive entry exceeds the bounded entry limit.");
            }

            totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
            if (totalExpandedBytes > MaxArchiveExpandedBytes)
            {
                throw new InvalidDataException("DOCX expanded size exceeds the bounded archive limit.");
            }

            if (entry.Length > 1024)
            {
                double ratio = entry.Length / (double)Math.Max(entry.CompressedLength, 1);
                if (ratio > MaxCompressionRatio)
                {
                    throw new InvalidDataException("DOCX archive contains a suspicious compression ratio.");
                }
            }
        }
    }

    private static void ValidateArchivePath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)
            || fullName.StartsWith('/', StringComparison.Ordinal)
            || fullName.StartsWith('\\')
            || fullName.Contains(":", StringComparison.Ordinal))
        {
            throw new InvalidDataException("DOCX archive contains an unsafe entry path.");
        }

        string normalized = fullName.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => string.Equals(part, "..", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("DOCX archive contains a parent-directory traversal entry.");
        }
    }

    private static string ExtractDocxText(ZipArchiveEntry entry)
    {
        XmlReaderSettings settings = new()
        {
            Async = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxArchiveEntryBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        StringBuilder output = new();
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        output.Append(reader.ReadElementContentAsString());
                        break;
                    case "tab":
                        output.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        AppendNewline(output);
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement
                && string.Equals(reader.LocalName, "p", StringComparison.Ordinal))
            {
                AppendNewline(output);
            }

            if (output.Length > MaxSourceBytes)
            {
                throw new InvalidDataException("DOCX extracted text exceeds the bounded text limit.");
            }
        }

        return output.ToString().TrimEnd('\r', '\n');
    }

    private static string ExtractHtmlText(string html)
    {
        StringBuilder output = new(Math.Min(html.Length, MaxSourceBytes));
        int index = 0;
        while (index < html.Length)
        {
            if (html[index] != '<')
            {
                int nextTag = html.IndexOf('<', index);
                if (nextTag < 0)
                {
                    nextTag = html.Length;
                }

                AppendDecodedHtml(output, html.AsSpan(index, nextTag - index));
                index = nextTag;
                continue;
            }

            int tagEnd = html.IndexOf('>', index + 1);
            if (tagEnd < 0)
            {
                AppendDecodedHtml(output, html.AsSpan(index));
                break;
            }

            ReadOnlySpan<char> tag = html.AsSpan(index + 1, tagEnd - index - 1).Trim();
            string tagName = ReadTagName(tag);
            bool closing = tag.Length > 0 && tag[0] == '/';
            if (!closing && IsDiscardedHtmlContainer(tagName))
            {
                index = SkipHtmlContainer(html, tagEnd + 1, tagName);
                continue;
            }

            if (IsHtmlLineBreak(tagName))
            {
                AppendNewline(output);
            }

            index = tagEnd + 1;
            if (output.Length > MaxSourceBytes)
            {
                throw new InvalidDataException("HTML extracted text exceeds the bounded text limit.");
            }
        }

        return NormalizeExtractedWhitespace(output.ToString());
    }

    private static int SkipHtmlContainer(string html, int start, string tagName)
    {
        string closingTag = "</" + tagName;
        int closeStart = html.IndexOf(closingTag, start, StringComparison.OrdinalIgnoreCase);
        if (closeStart < 0)
        {
            return html.Length;
        }

        int closeEnd = html.IndexOf('>', closeStart + closingTag.Length);
        return closeEnd < 0 ? html.Length : closeEnd + 1;
    }

    private static bool IsDiscardedHtmlContainer(string tagName) => tagName is
        "script" or "style" or "noscript" or "template" or "svg" or "math";

    private static bool IsHtmlLineBreak(string tagName) => tagName is
        "br" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre";

    private static string ReadTagName(ReadOnlySpan<char> tag)
    {
        if (tag.Length > 0 && tag[0] == '/')
        {
            tag = tag[1..].TrimStart();
        }

        int length = 0;
        while (length < tag.Length && (char.IsLetterOrDigit(tag[length]) || tag[length] is ':' or '-'))
        {
            length++;
        }

        return tag[..length].ToString().ToLowerInvariant();
    }

    private static void AppendDecodedHtml(StringBuilder output, ReadOnlySpan<char> content)
    {
        if (content.IsEmpty)
        {
            return;
        }

        output.Append(WebUtility.HtmlDecode(content.ToString()));
    }

    private static string NormalizeExtractedWhitespace(string value)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        StringBuilder output = new(normalized.Length);
        int blankLines = 0;
        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                blankLines++;
                if (blankLines > 1)
                {
                    continue;
                }
            }
            else
            {
                blankLines = 0;
            }

            if (output.Length > 0)
            {
                output.Append('\n');
            }

            output.Append(line);
        }

        return output.ToString().Trim();
    }

    private static void AppendNewline(StringBuilder output)
    {
        if (output.Length == 0 || output[^1] == '\n')
        {
            return;
        }

        output.Append('\n');
    }

    private static string DecodeText(byte[] source)
    {
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
            throw new InvalidDataException("Text import is not valid UTF-8 and has no supported Unicode BOM.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new(Math.Min(maxBytes, 64 * 1024));
        byte[] chunk = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Import source exceeds the {maxBytes:N0}-byte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildSuggestedTitle(string displayName)
    {
        string title = Path.GetFileNameWithoutExtension(displayName.Trim());
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Imported document";
        }

        return title.Length <= 200 ? title : title[..200];
    }

    private static string BuildProvenance(string kind, string displayName) =>
        $"local-import:{kind}:{Path.GetFileName(displayName.Trim())}";
}
