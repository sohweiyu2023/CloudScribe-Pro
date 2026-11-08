namespace CloudScribe.Architecture.Tests;

/// <summary>Guards the UI wiring to the behaviorally tested batch operations.</summary>
public sealed class BatchBinderRegressionTests
{
    [Fact]
    public void ArrangementCapturesSnapshotBeforeSavingAndRowsIncludeIdentity()
    {
        string source = BinderSource("Rows");
        Assert.Contains("var before = session.Items.ToArray();", source, StringComparison.Ordinal);
        Assert.Contains("rows.Select(x => x.Item.ItemId +", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftAndPendingImportUseDiskAwareReaders()
    {
        string source = BinderSource("Files");
        Assert.Contains("ctx.Importer.ReadDraftSourceAsync(item, session.Texts", source, StringComparison.Ordinal);
        Assert.Contains("ctx.Importer.ReadPendingAsync(item, session.Texts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (session.Texts.ContainsKey(item.ItemId)) continue;", source, StringComparison.Ordinal);
        int missing = source.IndexOf("if (missing.Count > 0)", StringComparison.Ordinal);
        int create = source.IndexOf("var batch = new NarrationBatch", missing, StringComparison.Ordinal);
        Assert.Contains("return;", source[missing..create], StringComparison.Ordinal);
    }

    private static string BinderSource(string part)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
                return File.ReadAllText(Path.Combine(directory.FullName, "src", "CloudScribe.App", "Composition", "GoogleTtsUsabilityHotfixBinder.Batch." + part + ".cs"));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }
}
