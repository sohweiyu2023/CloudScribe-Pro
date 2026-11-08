using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Architecture.Tests;

/// <summary>Small, exhaustive valid-space oracle plus deterministic greedy pair covering. Test-only.</summary>
internal static class ConstrainedPairwise
{
    internal sealed record Axis(string Name, string[] Values);
    internal sealed record Result(string[][] Cases, int ValidCandidates, int ExcludedCandidates, int ValidPairs);

    internal static Result Generate(IReadOnlyList<Axis> axes, Func<string[], bool> valid, int seed)
    {
        var candidates = Enumerate(axes, valid, out int excluded);
        var remaining = Pairs(candidates);
        int totalPairs = remaining.Count;
        // SHA-256 ordering is stable across OS/runtime versions, unlike framework Random implementation details.
        var ordered = candidates.OrderBy(row => Rank(seed, row), StringComparer.Ordinal)
            .ThenBy(Id, StringComparer.Ordinal).Select(row => (Row: row, Pairs: Pairs([row]))).ToArray();
        List<string[]> selected = [];
        while (remaining.Count > 0)
        {
            var next = ordered.MaxBy(candidate => candidate.Pairs.Count(remaining.Contains));
            if (!next.Pairs.Any(remaining.Contains)) throw new InvalidOperationException("Uncovered valid pairs cannot be reached.");
            selected.Add(next.Row);
            remaining.ExceptWith(next.Pairs);
        }
        return new(selected.ToArray(), candidates.Length, excluded, totalPairs);
    }

    internal static int Verify(IReadOnlyList<Axis> axes, Func<string[], bool> valid, IReadOnlyList<string[]> selected)
    {
        // Re-enumerate the feasible space instead of trusting the greedy algorithm's progress counters.
        var feasible = Enumerate(axes, valid, out _).Select(Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var row in selected)
        {
            if (row.Length != axes.Count || row.Where((value, index) => !axes[index].Values.Contains(value, StringComparer.Ordinal)).Any())
                throw new InvalidOperationException("Invalid matrix case shape or value: " + Id(row));
            if (!feasible.Contains(Id(row))) throw new InvalidOperationException("Invalid matrix case: " + Id(row));
            if (!seen.Add(Id(row))) throw new InvalidOperationException("Duplicate matrix case: " + Id(row));
        }
        int checkedPairs = 0;
        for (int left = 0; left < axes.Count; left++)
            for (int right = left + 1; right < axes.Count; right++)
                foreach (string a in axes[left].Values)
                    foreach (string b in axes[right].Values)
                    {
                        bool possible = feasible.Any(id =>
                        {
                            var row = id.Split('|');
                            return string.Equals(row[left], a, StringComparison.Ordinal) && string.Equals(row[right], b, StringComparison.Ordinal);
                        });
                        if (!possible) continue;
                        checkedPairs++;
                        if (!selected.Any(row => string.Equals(row[left], a, StringComparison.Ordinal) && string.Equals(row[right], b, StringComparison.Ordinal)))
                            throw new InvalidOperationException($"Missing valid pair: {axes[left].Name}={a}; {axes[right].Name}={b}");
                    }
        return checkedPairs;
    }

    internal static string Id(string[] row) => string.Join('|', row);

    private static string Rank(int seed, string[] row) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(seed.ToString(CultureInfo.InvariantCulture) + ":" + Id(row))));

    private static string[][] Enumerate(IReadOnlyList<Axis> axes, Func<string[], bool> valid, out int excluded)
    {
        if (axes.Count is < 2 or > 12) throw new ArgumentException("Pairwise coverage needs between two and twelve axes.", nameof(axes));
        long combinations = 1;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var axis in axes)
        {
            if (string.IsNullOrWhiteSpace(axis.Name) || !names.Add(axis.Name) || axis.Values.Length == 0
                || axis.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('|', StringComparison.Ordinal))
                || axis.Values.Distinct(StringComparer.Ordinal).Count() != axis.Values.Length)
                throw new ArgumentException("Axes require unique names and nonempty distinct canonical values.", nameof(axes));
            combinations *= axis.Values.Length;
            if (combinations > 8192) throw new ArgumentException("The test oracle is bounded to 8192 candidate rows.", nameof(axes));
        }
        List<string[]> rows = [[]];
        foreach (var axis in axes) rows = rows.SelectMany(row => axis.Values.Select(value => row.Append(value).ToArray())).ToList();
        var candidates = rows.Where(valid).ToArray();
        excluded = rows.Count - candidates.Length;
        if (candidates.Length == 0) throw new InvalidOperationException("Constraints exclude every candidate; coverage is not vacuous success.");
        return candidates;
    }

    private static HashSet<string> Pairs(IEnumerable<string[]> rows)
    {
        HashSet<string> pairs = new(StringComparer.Ordinal);
        foreach (var row in rows)
            for (int left = 0; left < row.Length; left++)
                for (int right = left + 1; right < row.Length; right++)
                    pairs.Add(string.Create(CultureInfo.InvariantCulture, $"{left}:{row[left]}|{right}:{row[right]}"));
        return pairs;
    }
}
