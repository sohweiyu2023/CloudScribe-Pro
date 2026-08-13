using System.IO.Compression;
using System.Text;
using System.Xml;

namespace CloudScribe.Infrastructure.Files;

internal static class BoundedDocxTextExtractor
{
    private const long MaxArchiveExpandedBytes = 128L * 1024 * 1024;
    private const long MaxArchiveEntryBytes = 32L * 1024 * 1024;
    private const int MaxArchiveEntries = 2048;
    private const double MaxCompressionRatio = 200.0;

    public static string Extract(byte[] source, int maxTextCharacters)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextCharacters);
        using MemoryStream archiveStream = new(source, writable: false);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        ValidateArchive(archive);
        ZipArchiveEntry documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX does not contain word/document.xml.");
        if (documentEntry.Length > MaxArchiveEntryBytes)
        {
            throw new InvalidDataException("DOCX main document part exceeds the configured entry limit.");
        }

        return ReadDocumentXml(documentEntry, maxTextCharacters);
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
            ValidateEntry(entry);
            totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
            if (totalExpandedBytes > MaxArchiveExpandedBytes)
            {
                throw new InvalidDataException("DOCX expanded size exceeds the configured archive limit.");
            }
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        ValidateEntryPath(entry.FullName);
        if (entry.Length > MaxArchiveEntryBytes)
        {
            throw new InvalidDataException("DOCX archive entry exceeds the configured entry limit.");
        }

        if (entry.Length <= 1024)
        {
            return;
        }

        double ratio = entry.Length / (double)Math.Max(entry.CompressedLength, 1);
        if (ratio > MaxCompressionRatio)
        {
            throw new InvalidDataException("DOCX archive contains a suspicious compression ratio.");
        }
    }

    private static void ValidateEntryPath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)
            || fullName.StartsWith('/')
            || fullName.StartsWith('\\')
            || fullName.Contains(':'))
        {
            throw new InvalidDataException("DOCX archive contains an unsafe entry path.");
        }

        string normalized = fullName.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => string.Equals(part, "..", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("DOCX archive contains an unsafe parent-directory entry.");
        }
    }

    private static string ReadDocumentXml(ZipArchiveEntry entry, int maxTextCharacters)
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
            AppendCurrentNode(reader, output);
            if (output.Length > maxTextCharacters)
            {
                throw new InvalidDataException("DOCX extracted text exceeds the configured text limit.");
            }
        }

        return output.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendCurrentNode(XmlReader reader, StringBuilder output)
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

            return;
        }

        if (reader.NodeType == XmlNodeType.EndElement
            && string.Equals(reader.LocalName, "p", StringComparison.Ordinal))
        {
            AppendNewline(output);
        }
    }

    private static void AppendNewline(StringBuilder output)
    {
        if (output.Length > 0 && output[^1] != '\n')
        {
            output.Append('\n');
        }
    }
}
