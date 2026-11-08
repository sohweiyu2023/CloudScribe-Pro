using System.Text.Json;

namespace CloudScribe.Architecture.Tests;

public sealed class ConstrainedPairwiseTests
{
    private static readonly ConstrainedPairwise.Axis[] TinyAxes = [new("a", ["a0", "a1"]), new("b", ["b0", "b1"]), new("c", ["c0", "c1"])];

    [Fact]
    public void WorkflowMatrixInventoryIsDeterministicAndCoversEveryFeasiblePair()
    {
        var generated = ConstrainedPairwise.Generate(WorkflowMatrixCases.Axes, WorkflowMatrixCases.IsValid, WorkflowMatrixCases.Seed);
        var ids = WorkflowMatrixCases.ReadIds();
        Assert.Equal(ids, generated.Cases.Select(ConstrainedPairwise.Id), StringComparer.Ordinal);
        Assert.Equal(ids, ConstrainedPairwise.Generate(WorkflowMatrixCases.Axes, WorkflowMatrixCases.IsValid, WorkflowMatrixCases.Seed).Cases.Select(ConstrainedPairwise.Id), StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(WorkflowMatrixCases.ManifestPath));
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(WorkflowMatrixCases.Axes.Select(axis => axis.Name), manifest.RootElement.GetProperty("axes").EnumerateObject().Select(axis => axis.Name), StringComparer.Ordinal);
        foreach (var axis in WorkflowMatrixCases.Axes)
            Assert.Equal(axis.Values, manifest.RootElement.GetProperty("axes").GetProperty(axis.Name).EnumerateArray().Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(manifest.RootElement.GetProperty("validCandidates").GetInt32(), generated.ValidCandidates);
        Assert.Equal(manifest.RootElement.GetProperty("excludedCandidates").GetInt32(), generated.ExcludedCandidates);
        Assert.Equal(manifest.RootElement.GetProperty("validPairs").GetInt32(), generated.ValidPairs);
        Assert.Equal(generated.ValidPairs, ConstrainedPairwise.Verify(WorkflowMatrixCases.Axes, WorkflowMatrixCases.IsValid, ids.Select(id => id.Split('|')).ToArray()));
        Assert.InRange(ids.Length, 1, 80);
        TestContext.Current.TestOutputHelper!.WriteLine("Seed {0}: {1} concrete cases; {2} feasible pairs; {3} valid / {4} excluded candidates. Not Cartesian coverage.",
            WorkflowMatrixCases.Seed, ids.Length, generated.ValidPairs, generated.ValidCandidates, generated.ExcludedCandidates);
    }

    [Fact]
    public void ImpossibleConstraintsFailRatherThanClaimVacuousCoverage()
    {
        Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Generate(TinyAxes, _ => false, 7));
        Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Verify(TinyAxes, _ => false, []));
    }

    [Fact]
    public void IndependentVerifierDetectsMissingPairsEvenWhenEveryValueAppears()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Verify(TinyAxes, _ => true,
            [["a0", "b0", "c0"], ["a1", "b1", "c1"]]));
        Assert.Contains("Missing valid pair", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstraintsRemoveImpossiblePairsButRetainAllReachableOnes()
    {
        static bool Valid(string[] row) => !string.Equals(row[0], "a1", StringComparison.Ordinal) || string.Equals(row[1], "b1", StringComparison.Ordinal);
        var result = ConstrainedPairwise.Generate(TinyAxes, Valid, 17);
        Assert.Equal(6, result.ValidCandidates);
        Assert.Equal(2, result.ExcludedCandidates);
        Assert.Equal(11, ConstrainedPairwise.Verify(TinyAxes, Valid, result.Cases));
        Assert.All(result.Cases, row => Assert.True(Valid(row)));
        Assert.Equal(result.Cases.Select(ConstrainedPairwise.Id), ConstrainedPairwise.Generate(TinyAxes, Valid, 17).Cases.Select(ConstrainedPairwise.Id), StringComparer.Ordinal);
    }

    [Fact]
    public void InvalidOrDuplicateCasesCannotPassCoverageValidation()
    {
        var result = ConstrainedPairwise.Generate(TinyAxes, _ => true, 7);
        Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Verify(TinyAxes, _ => true, [.. result.Cases, result.Cases[0]]));
        Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Verify(TinyAxes, _ => true, [.. result.Cases, ["unknown", "b0", "c0"]]));
        Assert.Throws<InvalidOperationException>(() => ConstrainedPairwise.Verify(TinyAxes, _ => true, [.. result.Cases, ["a0"]]));
    }

    [Fact]
    public void MalformedAxesAndExcessiveCandidateSpacesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate([new("empty", []), TinyAxes[0]], _ => true, 1));
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate([new("a", ["a0", "a0"]), TinyAxes[1]], _ => true, 1));
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate([TinyAxes[0], TinyAxes[0]], _ => true, 1));
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate([new("a", ["ambiguous|value"]), TinyAxes[1]], _ => true, 1));
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate([TinyAxes[0]], _ => true, 1));
        var huge = Enumerable.Range(0, 12).Select(index => new ConstrainedPairwise.Axis(index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["0", "1", "2"])).ToArray();
        Assert.Throws<ArgumentException>(() => ConstrainedPairwise.Generate(huge, _ => true, 1));
    }
}
