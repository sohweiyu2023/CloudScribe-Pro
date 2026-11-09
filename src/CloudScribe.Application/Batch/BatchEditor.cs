namespace CloudScribe.Application.Batch;

/// <summary>Arranging and removing files before they start. Only files that are waiting and have no saved job can be
/// moved; failed pre-import files may also be removed. Started, finished and uncertain files keep their history. Every change
/// keeps the relative order of the files it moves, so a multi-file move never scrambles them.</summary>
public static class BatchEditor
{
    /// <summary>True when the file has not started and may be moved or removed.</summary>
    public static bool CanArrange(BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.State == BatchItemState.Ready && item.JobId is null;
    }

    /// <summary>True when the file may be taken out: waiting or failed before import, or an unusable draft file.</summary>
    public static bool CanRemove(BatchItem item, bool draft)
    {
        ArgumentNullException.ThrowIfNull(item);
        return CanArrange(item) || BatchDocumentImporter.NeedsImport(item) || draft && item.State == BatchItemState.Excluded;
    }

    /// <summary>Moves the arrangeable files among <paramref name="ids"/> so that they sit, in their current relative
    /// order, just before the file that is currently at <paramref name="insertIndex"/> (0 … count; count means the end).
    /// Files that cannot be arranged are ignored and never moved.</summary>
    public static IReadOnlyList<BatchItem> MoveTo(IReadOnlyList<BatchItem> items, IReadOnlyCollection<Guid> ids, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ids);
        insertIndex = Math.Clamp(insertIndex, 0, items.Count);
        var moving = items.Where(x => ids.Contains(x.ItemId) && CanArrange(x)).ToArray();
        if (moving.Length == 0) return items;
        var movingIds = moving.Select(x => x.ItemId).ToHashSet();
        // The gap is counted among the files that stay, so dropping a block inside itself leaves it where it was.
        int before = items.Take(insertIndex).Count(x => !movingIds.Contains(x.ItemId));
        var staying = items.Where(x => !movingIds.Contains(x.ItemId)).ToList();
        staying.InsertRange(before, moving);
        return staying.Select(x => x.ItemId).SequenceEqual(items.Select(x => x.ItemId)) ? items : staying;
    }

    /// <summary>Moves each arrangeable selected file one place up (negative) or down (positive), as a block: a run of
    /// selected files moves together past the next unselected file, and files at the edge stay put.</summary>
    public static IReadOnlyList<BatchItem> MoveBy(IReadOnlyList<BatchItem> items, IReadOnlyCollection<Guid> ids, int direction)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ids);
        if (direction == 0) return items;
        var list = items.ToList();
        bool Selected(int index) => ids.Contains(list[index].ItemId) && CanArrange(list[index]);
        bool changed = false;
        if (direction < 0)
        {
            for (int i = 1; i < list.Count; i++)
                if (Selected(i) && !Selected(i - 1) && !IsPinnedEdge(list, i - 1, ids))
                { (list[i - 1], list[i]) = (list[i], list[i - 1]); changed = true; }
        }
        else
        {
            for (int i = list.Count - 2; i >= 0; i--)
                if (Selected(i) && !Selected(i + 1) && !IsPinnedEdge(list, i + 1, ids))
                { (list[i + 1], list[i]) = (list[i], list[i + 1]); changed = true; }
        }
        return changed ? list : items;
    }

    // A selected file that cannot be arranged is a fixed point: nothing jumps over it as part of its own selection.
    private static bool IsPinnedEdge(List<BatchItem> list, int index, IReadOnlyCollection<Guid> ids) =>
        ids.Contains(list[index].ItemId) && !CanArrange(list[index]);

    /// <summary>Removes the selected files. In a draft they are taken out of the list; in a saved batch they are kept
    /// as "removed by you", so the saved history and any approval stay explainable. Started files are never removed.</summary>
    public static IReadOnlyList<BatchItem> Remove(IReadOnlyList<BatchItem> items, IReadOnlyCollection<Guid> ids, bool draft)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ids);
        if (draft) return items.Where(x => !(ids.Contains(x.ItemId) && CanRemove(x, draft: true))).ToArray();
        return items.Select(x => ids.Contains(x.ItemId) && CanRemove(x, draft: false)
            ? x with { State = BatchItemState.Excluded, Exclusion = BatchExclusion.RemovedByUser } : x).ToArray();
    }

    /// <summary>Puts back an earlier arrangement (Undo). Files that started since the snapshot keep their current state.
    /// Files added since the snapshot stay at the end when <paramref name="keepAdded"/> is set (a saved batch, whose
    /// added files are already in the library); in a draft, undoing an add takes those files out again. A started file
    /// is always kept, so an undo never loses progress.</summary>
    public static IReadOnlyList<BatchItem> Restore(IReadOnlyList<BatchItem> current, IReadOnlyList<BatchItem> snapshot, bool keepAdded = true)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = current.ToDictionary(x => x.ItemId);
        var result = new List<BatchItem>(Math.Max(current.Count, snapshot.Count));
        foreach (var earlier in snapshot)
        {
            if (!now.TryGetValue(earlier.ItemId, out var latest))
            {
                // Taken out of a draft since the snapshot: bring it back as it was.
                result.Add(earlier);
                continue;
            }
            result.Add(CanArrange(latest) || latest.State == BatchItemState.Excluded && latest.Exclusion == BatchExclusion.RemovedByUser
                ? latest with { State = earlier.State, Exclusion = earlier.Exclusion }
                : latest);
        }
        var known = snapshot.Select(x => x.ItemId).ToHashSet();
        result.AddRange(current.Where(x => !known.Contains(x.ItemId) && (keepAdded || !CanRemove(x, draft: true))));
        return result;
    }

    /// <summary>Files whose source path is already in the list (the same TXT dropped or picked twice). Each TXT makes
    /// exactly one MP3; two different files with identical text are still two tracks.</summary>
    public static bool IsAlreadyListed(IReadOnlyList<BatchItem> items, string? sourcePath) =>
        Normalize(sourcePath) is { } full && ListedPaths(items).Contains(full);

    /// <summary>The listed source paths, normalised once, so checking thousands of new files against thousands of
    /// listed ones stays linear instead of re-normalising every listed path for every new file.</summary>
    public static IReadOnlySet<string> ListedPaths(IReadOnlyList<BatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in items)
            if (item.Exclusion != BatchExclusion.RemovedByUser && Normalize(item.SourcePath) is { } full) paths.Add(full);
        return paths;
    }

    /// <summary>The full path, or null for an empty or malformed path (which then simply cannot match).</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
