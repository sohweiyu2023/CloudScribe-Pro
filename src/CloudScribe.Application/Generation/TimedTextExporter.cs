using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Application.Generation;

public static class TimedTextExporter
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public static string Export(TimedTextTrack track, TimedTextExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        return format switch
        {
            TimedTextExportFormat.Json => ExportJson(track),
            TimedTextExportFormat.WebVtt => ExportWebVtt(track),
            TimedTextExportFormat.SubRip => ExportSubRip(track),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static string ExportJson(TimedTextTrack track)
    {
        var payload = new
        {
            schema = "cloudscribe.timed-text.v1",
            cues = track.Cues.Select(static cue => new
            {
                sequence = cue.Sequence,
                startMilliseconds = checked((long)cue.Start.TotalMilliseconds),
                endMilliseconds = checked((long)cue.End.TotalMilliseconds),
                text = cue.Text,
                provenanceId = cue.ProvenanceId,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, WebJsonOptions);
    }

    private static string ExportWebVtt(TimedTextTrack track)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in track.Cues)
        {
            builder.Append(cue.Sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTimestamp(cue.Start, '.')).Append(" --> ").Append(FormatTimestamp(cue.End, '.')).Append('\n');
            builder.Append("NOTE provenance:").Append(SanitizeSingleLine(cue.ProvenanceId)).Append('\n');
            builder.Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Append("\n\n");
        }
        return builder.ToString();
    }

    private static string ExportSubRip(TimedTextTrack track)
    {
        var builder = new StringBuilder();
        foreach (var cue in track.Cues)
        {
            builder.Append(cue.Sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTimestamp(cue.Start, ',')).Append(" --> ").Append(FormatTimestamp(cue.End, ',')).Append('\n');
            builder.Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Append('\n');
            builder.Append("[provenance:").Append(SanitizeSingleLine(cue.ProvenanceId)).Append("]\n\n");
        }
        return builder.ToString();
    }

    private static string FormatTimestamp(TimeSpan value, char millisecondSeparator)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
        var totalHours = (long)value.TotalHours;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}{millisecondSeparator}{value.Milliseconds:000}");
    }

    private static string SanitizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
