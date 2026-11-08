using System.Globalization;
using System.Text;
using CloudScribe.Application.Batch;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Writes a playlist (<c>.m3u8</c>, in batch order) and a CSV summary beside the MP3s once files finish.
/// Existing files are never overwritten; a numbered name is used instead. Contains file names, not article text.</summary>
public static class BatchManifestWriter
{
    public static (string Playlist, string Summary) Write(NarrationBatch queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        System.IO.Directory.CreateDirectory(queue.OutputFolder);
        string stem = SafeStem(queue.Name);
        string playlist = Unique(Path.Combine(queue.OutputFolder, stem + " playlist.m3u8"));
        var m3u = new StringBuilder("#EXTM3U\n");
        foreach (var item in queue.Items.Where(x => x.State == BatchItemState.Completed && x.OutputPath is not null).OrderBy(x => x.Order))
            m3u.Append(CultureInfo.InvariantCulture, $"#EXTINF:-1,{BatchPlanner.TitleOf(item.SourceFileName)}\n{Path.GetFileName(item.OutputPath)}\n");
        File.WriteAllText(playlist, m3u.ToString(), new UTF8Encoding(false));
        string summary = Unique(Path.Combine(queue.OutputFolder, stem + " summary.csv"));
        var csv = new StringBuilder("Order,Source file,Output MP3,Status,Reason,Characters,Active seconds,Estimated maximum,Reserved,Currency\n");
        foreach (var item in queue.Items.OrderBy(x => x.Order))
            csv.Append(CultureInfo.InvariantCulture,
                $"{item.Order},{Csv(item.SourceFileName)},{Csv(item.OutputFileName)},{item.State},{Csv(item.ReasonCode ?? (item.Exclusion == BatchExclusion.None ? string.Empty : item.Exclusion.ToString()))},{item.Characters},{item.ActiveMilliseconds / 1000},{Money(item.EstimatedMaximumMinorUnits, queue.CurrencyScale)},{Money(item.ReservedMinorUnits, queue.CurrencyScale)},{queue.Currency}\n");
        File.WriteAllText(summary, csv.ToString(), new UTF8Encoding(true));
        return (playlist, summary);
    }

    private static string Money(long minor, int scale) =>
        (minor / (decimal)Math.Pow(10, scale)).ToString("0.00####", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        // Prevent spreadsheet formula injection and quote every field.
        string safe = value.Length > 0 && "=+-@\t\r".Contains(value[0], StringComparison.Ordinal) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SafeStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        string stem = new(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        stem = stem.Trim().TrimEnd('.');
        return stem.Length == 0 ? "Batch" : stem[..Math.Min(stem.Length, 120)];
    }

    /// <summary>A path in <paramref name="folder"/> that does not exist yet, adding " (n)" if needed.</summary>
    public static string FreePath(string folder, string stem, string extension) =>
        Unique(Path.Combine(Path.GetFullPath(folder), SafeStem(stem) + extension));

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string folder = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){extension}"));
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
