using System.Globalization;

namespace CloudScribe.Application.Batch;

/// <summary>Turns selected files into an ordered, reviewable batch. It never changes source text and never merges or
/// drops files: every readable file with text maps to exactly one output name, unique in the batch and in the folder.
/// Identical text is kept and only noted, because the same words may legitimately appear in two tracks.</summary>
public static class BatchPlanner
{
    /// <param name="files">Selected files, already decoded; their order is replaced by natural file-name order.</param>
    /// <param name="outputNameFor">Converts a title to a Windows-safe file name with the .mp3 extension.</param>
    /// <param name="outputExists">True when a file of that name already exists in the output folder.</param>
    /// <param name="numberTracks">Prefix each name with its track number unless the title already starts with one.</param>
    public static IReadOnlyList<BatchItem> Plan(IEnumerable<BatchSourceFile> files, Func<string, string> outputNameFor,
        Func<string, bool> outputExists, bool numberTracks = false) => Append([], files, outputNameFor, outputExists, numberTracks);

    /// <summary>Adds files after the existing ones. The existing (possibly hand-arranged) order is kept; only the new
    /// files are put in natural order among themselves (by name, or as given when <paramref name="keepGivenOrder"/> is
    /// set because the caller already sorted them, for example by folder and then name).</summary>
    public static IReadOnlyList<BatchItem> Append(IReadOnlyList<BatchItem> existing, IEnumerable<BatchSourceFile> files,
        Func<string, string> outputNameFor, Func<string, bool> outputExists, bool numberTracks = false, bool keepGivenOrder = false)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(outputNameFor);
        ArgumentNullException.ThrowIfNull(outputExists);
        var items = existing.ToList();
        foreach (var file in keepGivenOrder ? files : files.OrderBy(x => x.FileName, BatchNaturalOrder.Instance))
        {
            var problem = file.Problem;
            if (problem == BatchExclusion.None && string.IsNullOrWhiteSpace(file.Text)) problem = BatchExclusion.Empty;
            items.Add(new BatchItem(Guid.NewGuid(), items.Count + 1, file.FileName, file.Sha256, file.Bytes,
                file.Text is null ? 0 : CountCharacters(file.Text), string.Empty,
                problem == BatchExclusion.None ? BatchItemState.Ready : BatchItemState.Excluded, problem, SourcePath: file.SourcePath));
        }
        return AssignOutputNames(items, outputNameFor, outputExists, numberTracks);
    }

    /// <summary>Recomputes order numbers and output names after the user reorders, removes or adds files.
    /// Names of files that have started (or finished) are never changed.</summary>
    public static IReadOnlyList<BatchItem> AssignOutputNames(IReadOnlyList<BatchItem> items, Func<string, string> outputNameFor,
        Func<string, bool> outputExists, bool numberTracks = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(outputNameFor);
        ArgumentNullException.ThrowIfNull(outputExists);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(IsLocked)) if (item.OutputFileName.Length > 0) used.Add(item.OutputFileName);
        int width = TrackWidth(items);
        var result = new List<BatchItem>(items.Count);
        // Identical text is noted against the earliest file with that text in the current order, so the note stays
        // right after the user moves or removes files (it is never an order number from when the file was added).
        var firstWithText = new Dictionary<string, int>(StringComparer.Ordinal);
        int track = 0;
        foreach (var item in items)
        {
            int order = result.Count + 1;
            int? same = null;
            if (item.IsIncluded)
            {
                track++;
                if (firstWithText.TryGetValue(item.SourceSha256, out int earlier)) same = earlier;
                else firstWithText[item.SourceSha256] = order;
            }
            string name = item.OutputFileName;
            if (!IsLocked(item))
                name = item.IsIncluded ? UniqueName(PreferredName(item.SourceFileName, track, width, numberTracks, outputNameFor), used, outputExists) : string.Empty;
            if (name.Length > 0) used.Add(name);
            result.Add(item with { Order = order, OutputFileName = name, SameTextAs = same });
        }
        return result;
    }

    /// <summary>Digits used for track numbers: at least two, enough for every included file.</summary>
    public static int TrackWidth(IReadOnlyList<BatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Math.Max(2, items.Count(x => x.IsIncluded).ToString(CultureInfo.InvariantCulture).Length);
    }

    /// <summary>The name a file would get with no clash: its title, optionally prefixed with its track number.
    /// A title that already starts with a number (for example "01 Preface") is never numbered twice.</summary>
    public static string PreferredName(string sourceFileName, int track, int width, bool numberTracks, Func<string, string> outputNameFor)
    {
        ArgumentNullException.ThrowIfNull(outputNameFor);
        string title = TitleOf(sourceFileName);
        if (numberTracks && !StartsWithTrackNumber(title))
            title = track.ToString(new string('0', Math.Max(1, width)), CultureInfo.InvariantCulture) + " " + title;
        string name = outputNameFor(title);
        return FitOutputName(Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
    }

    /// <summary>Files whose output name had to change because the preferred name was taken (in this folder or batch).</summary>
    public static IReadOnlyList<BatchItem> Renamed(IReadOnlyList<BatchItem> items, Func<string, string> outputNameFor, bool numberTracks = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        int width = TrackWidth(items);
        var result = new List<BatchItem>();
        int track = 0;
        foreach (var item in items)
        {
            if (!item.IsIncluded) continue;
            track++;
            if (!IsLocked(item) && !string.Equals(item.OutputFileName, PreferredName(item.SourceFileName, track, width, numberTracks, outputNameFor), StringComparison.OrdinalIgnoreCase))
                result.Add(item);
        }
        return result;
    }

    public static bool StartsWithTrackNumber(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        int digits = 0;
        while (digits < title.Length && char.IsDigit(title[digits])) digits++;
        return digits > 0 && (digits == title.Length || title[digits] is ' ' or '.' or '_' or '-' or ')' or '　' or '、');
    }

    public static IReadOnlyList<BatchItem> Move(IReadOnlyList<BatchItem> items, Guid itemId, int offset)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        int index = list.FindIndex(x => x.ItemId == itemId);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return items;
        (list[index], list[target]) = (list[target], list[index]);
        return list;
    }

    public static IReadOnlyList<BatchItem> Remove(IReadOnlyList<BatchItem> items, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Select(x => x.ItemId == itemId && x.State == BatchItemState.Ready && x.JobId is null
            ? x with { State = BatchItemState.Excluded, Exclusion = BatchExclusion.RemovedByUser } : x).ToArray();
    }

    public static string TitleOf(string fileName)
    {
        string title = Path.GetFileNameWithoutExtension(fileName).Trim();
        return title.Length == 0 ? "Untitled" : title;
    }

    /// <summary>The library document title for a batch file: its name without extension, fitted to the library's title
    /// limit on a character boundary. Output MP3 names have their own filesystem-component limit, including numbering
    /// and collision suffixes; neither fitted name changes the source or narration text.</summary>
    public static string DocumentTitleOf(string fileName) =>
        CloudScribe.Application.Documents.DocumentTitleText.Fit(TitleOf(fileName), CloudScribe.Application.Documents.DocumentTitleText.LibraryMaximum);

    /// <summary>Unicode scalar count, shown to the user. The exact billable maximum comes from the provider-specific review.</summary>
    public static int CountCharacters(string text) => text.EnumerateRunes().Count();

    public static bool IsLocked(BatchItem item) => item.State is BatchItemState.Completed or BatchItemState.Running || item.JobId is not null;

    private static string UniqueName(string candidate, HashSet<string> used, Func<string, bool> exists)
    {
        string stem = Path.GetFileNameWithoutExtension(candidate), extension = Path.GetExtension(candidate);
        string name = candidate;
        for (int n = 2; used.Contains(name) || exists(name); n++)
            name = FitOutputName(stem, extension, string.Create(CultureInfo.InvariantCulture, $" ({n})"));
        return name;
    }

    private static string FitOutputName(string stem, string extension, string suffix = "")
    {
        // NTFS limits each component to 255 UTF-16 code units. Reserve the extension and collision suffix
        // before fitting whole text elements, so numbering/clashes cannot create an unexportable name.
        int available = 255 - extension.Length - suffix.Length;
        if (available < 1) throw new ArgumentException("The audio extension and suffix leave no room for a filename.", nameof(extension));
        string fitted = CloudScribe.Application.Documents.DocumentTitleText.Fit(stem, available);
        return (fitted.Length == 0 ? "\u2026" : fitted) + suffix + extension;
    }
}
