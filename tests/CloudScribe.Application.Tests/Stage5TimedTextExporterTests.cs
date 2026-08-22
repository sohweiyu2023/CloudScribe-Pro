using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Tests;

public sealed class Stage5TimedTextExporterTests
{
    private static readonly TimedTextTrack Track = new(
    [
        new TimedTextCue(1, TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(3500), "Hello", "segment:001"),
        new TimedTextCue(2, TimeSpan.FromMilliseconds(4000), TimeSpan.FromMilliseconds(6250), "World", "segment:002"),
    ]);

    [Fact]
    public void JsonExportPreservesTimingTextAndProvenance()
    {
        var json = TimedTextExporter.Export(Track, TimedTextExportFormat.Json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("cloudscribe.timed-text.v1", root.GetProperty("schema").GetString());
        var cues = root.GetProperty("cues");
        Assert.Equal(2, cues.GetArrayLength());
        Assert.Equal(1250, cues[0].GetProperty("startMilliseconds").GetInt64());
        Assert.Equal("segment:001", cues[0].GetProperty("provenanceId").GetString());
    }

    [Fact]
    public void WebVttUsesDotMillisecondsAndCarriesProvenanceNote()
    {
        var text = TimedTextExporter.Export(Track, TimedTextExportFormat.WebVtt);

        Assert.StartsWith("WEBVTT\n\n", text, StringComparison.Ordinal);
        Assert.Contains("00:00:01.250 --> 00:00:03.500", text, StringComparison.Ordinal);
        Assert.Contains("NOTE provenance:segment:001", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SubRipUsesCommaMillisecondsAndCarriesProvenanceMarker()
    {
        var text = TimedTextExporter.Export(Track, TimedTextExportFormat.SubRip);

        Assert.Contains("00:00:04,000 --> 00:00:06,250", text, StringComparison.Ordinal);
        Assert.Contains("[provenance:segment:002]", text, StringComparison.Ordinal);
    }
}
