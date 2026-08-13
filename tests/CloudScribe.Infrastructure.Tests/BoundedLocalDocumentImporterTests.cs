using System.IO.Compression;
using System.Text;
using System.Xml;
using CloudScribe.Application.Documents;
using CloudScribe.Infrastructure.Files;

namespace CloudScribe.Infrastructure.Tests;

public sealed class BoundedLocalDocumentImporterTests
{
    [Fact]
    public async Task PlainTextPreservesUnicode()
    {
        BoundedLocalDocumentImporter importer = new();
        const string source = "明 👨‍👩‍👧‍👦 e\u0301 العربية עברית\nsecond line";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(source));

        LocalDocumentImportResult result = await importer.ImportAsync(
            new(LocalDocumentImportKind.PlainText, "notes.txt", stream),
            TestContext.Current.CancellationToken);

        Assert.Equal("notes", result.SuggestedTitle);
        Assert.Equal(source, result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task HtmlIsImportedAsInertText()
    {
        BoundedLocalDocumentImporter importer = new();
        const string html = "<h1>Hello &amp; welcome</h1><script>alert('no')</script><p>Safe text</p>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(html));

        LocalDocumentImportResult result = await importer.ImportAsync(
            new(LocalDocumentImportKind.Html, "page.html", stream),
            TestContext.Current.CancellationToken);

        Assert.Contains("Hello & welcome", result.Text, StringComparison.Ordinal);
        Assert.Contains("Safe text", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", result.Text, StringComparison.Ordinal);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task DocxExtractsTextWithoutExecutingRelationships()
    {
        BoundedLocalDocumentImporter importer = new();
        using MemoryStream stream = CreateDocx(
            "<w:p><w:r><w:t>Hello</w:t></w:r></w:p><w:p><w:r><w:t>世界 👋🏽</w:t></w:r></w:p>");

        LocalDocumentImportResult result = await importer.ImportAsync(
            new(LocalDocumentImportKind.Docx, "book.docx", stream),
            TestContext.Current.CancellationToken);

        Assert.Equal("Hello\n世界 👋🏽", result.Text);
        Assert.Single(result.Warnings);
        Assert.StartsWith("local-import:docx-text-only:", result.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocxRejectsParentTraversalEntry()
    {
        BoundedLocalDocumentImporter importer = new();
        using MemoryStream stream = CreateDocx(
            "<w:p><w:r><w:t>Safe</w:t></w:r></w:p>",
            archive => WriteEntry(archive, "../escape.xml", "blocked"));

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(
            new(LocalDocumentImportKind.Docx, "unsafe.docx", stream),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DocxRejectsDtdDeclarations()
    {
        BoundedLocalDocumentImporter importer = new();
        const string xml = "<!DOCTYPE w:document [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>";
        using MemoryStream stream = CreateRawDocx(xml);

        await Assert.ThrowsAsync<XmlException>(() => importer.ImportAsync(
            new(LocalDocumentImportKind.Docx, "dtd.docx", stream),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DocxRejectsSuspiciousCompressionRatio()
    {
        BoundedLocalDocumentImporter importer = new();
        using MemoryStream stream = CreateDocx(
            "<w:p><w:r><w:t>Safe</w:t></w:r></w:p>",
            archive => WriteEntry(archive, "word/filler.bin", new string('A', 512 * 1024)));

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(
            new(LocalDocumentImportKind.Docx, "compressed.docx", stream),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeclaredOversizeSourceFailsBeforeReading()
    {
        BoundedLocalDocumentImporter importer = new();
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("small"));

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(
            new(LocalDocumentImportKind.PlainText, "large.txt", stream, 33L * 1024 * 1024),
            TestContext.Current.CancellationToken));
    }

    private static MemoryStream CreateDocx(string body, Action<ZipArchive>? addEntries = null) =>
        CreateRawDocx(WrapWordDocument(body), addEntries);

    private static MemoryStream CreateRawDocx(string documentXml, Action<ZipArchive>? addEntries = null)
    {
        MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "word/document.xml", documentXml);
            addEntries?.Invoke(archive);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string WrapWordDocument(string body) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>";
}
