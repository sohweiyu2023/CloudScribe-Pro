#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Globalization;
using System.Security.Cryptography;
using CloudScribe.Application.Batch;
using CloudScribe.Infrastructure.Batch;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class BatchNarrationTests
{
    [Theory]
    [InlineData(false, "a")]
    [InlineData(true, "a")]
    [InlineData(false, "\U0001F600")]
    [InlineData(true, "e\u0301")]
    public async Task LongNumberedAndCollidingNamesRemainExportableOnWindows(bool numbered, string element)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("This test checks Windows UTF-16 filename component limits."); return; }
        using var w = new Workspace();
        string stem = string.Concat(Enumerable.Repeat(element, 251 / element.Length)); // A legal near-limit TXT component.
        string preferred = BatchPlanner.PreferredName(stem + ".txt", 1, 2, numbered, t => t + ".mp3");
        string occupied = Path.Combine(w.Output, preferred);
        await File.WriteAllTextAsync(occupied, "existing unrelated audio", Token);
        var items = BatchPlanner.Plan([new(stem + ".txt", "hash1", 1, "one"), new(stem + ".txt", "hash2", 1, "two")],
            t => t + ".mp3", n => File.Exists(Path.Combine(w.Output, n)), numbered);
        Assert.EndsWith(" (2).mp3", items[0].OutputFileName, StringComparison.Ordinal);
        if (numbered) Assert.StartsWith("01 ", items[0].OutputFileName, StringComparison.Ordinal);
        else Assert.EndsWith(" (3).mp3", items[1].OutputFileName, StringComparison.Ordinal);
        Assert.Equal(2, items.Select(x => x.OutputFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(BatchPlanner.Renamed(items.Take(1).ToArray(), t => t + ".mp3", numbered));
        string source = Path.Combine(w.Root, "source.mp3");
        byte[] audio = "synthetic verified audio"u8.ToArray();
        await File.WriteAllBytesAsync(source, audio, Token);
        string hash = Convert.ToHexStringLower(SHA256.HashData(audio));
        foreach (var item in items)
        {
            Assert.InRange(item.OutputFileName.Length, 1, 255);
            string destination = Path.Combine(w.Output, item.OutputFileName);
            await BatchOutputFiles.CopyVerifiedAsync(source, hash, audio.Length, destination, Token);
            Assert.True(await BatchOutputFiles.MatchesAsync(destination, hash, audio.Length, Token));
            await BatchOutputFiles.CopyVerifiedAsync(source, hash, audio.Length, destination, Token);
        }
        Assert.Equal("existing unrelated audio", await File.ReadAllTextAsync(occupied, Token));
        Assert.Empty(Directory.GetFiles(w.Output, "*.partial"));
    }

    [Theory]
    [InlineData("\U0001F600")]
    [InlineData("e\u0301")]
    [InlineData("\U0001F469\u200D\U0001F4BB")]
    public void FilenameFittingNeverSplitsGraphemesIncludingCollisionSuffixes(string element)
    {
        string stem = string.Concat(Enumerable.Repeat(element, 180));
        var items = BatchPlanner.Plan([new(stem + ".txt", "hash1", 1, "one"), new(stem + ".txt", "hash2", 1, "two")], t => t + ".mp3", _ => false);
        foreach (var item in items)
        {
            string fitted = Path.GetFileNameWithoutExtension(item.OutputFileName);
            if (fitted.EndsWith(" (2)", StringComparison.Ordinal)) fitted = fitted[..^4];
            Assert.InRange(item.OutputFileName.Length, 1, 255);
            Assert.Equal(0, fitted.Length % element.Length);
            Assert.Equal(string.Concat(Enumerable.Repeat(element, fitted.Length / element.Length)), fitted);
            Assert.Equal(fitted.Length / element.Length, StringInfo.ParseCombiningCharacters(fitted).Length);
        }
    }

    [Fact]
    public void OversizedGraphemeHasABoundedOutputPlaceholderAndLockedNamesAreNeverRewritten()
    {
        Assert.Equal("name..mp3", BatchPlanner.PreferredName("name..txt", 1, 2, false, t => t + ".mp3"));
        string stem = "e" + new string('\u0301', 300);
        Assert.Equal("\u2026.mp3", BatchPlanner.PreferredName(stem + ".txt", 1, 2, false, t => t + ".mp3"));
        string retained = new string('a', 260) + ".mp3";
        var items = new[]
        {
            Arrangeable(1, "one.txt") with { OutputFileName = retained, State = BatchItemState.Completed },
            Arrangeable(2, "two.txt") with { OutputFileName = retained, State = BatchItemState.Running },
            Arrangeable(3, "three.txt") with { OutputFileName = retained, JobId = Guid.NewGuid() }
        };
        Assert.All(BatchPlanner.AssignOutputNames(items, t => t + ".mp3", _ => true, true), x => Assert.Equal(retained, x.OutputFileName));
    }

    [Fact]
    public async Task FittedNamesPreserveContentIdentityAndCannotHideApprovalInvalidation()
    {
        using var w = new Workspace();
        var batch = await w.CreateAsync([new string('a', 251) + ".txt"]);
        var profile = Profile(w.Root);
        var consent = Consent(batch, profile);
        string occupied = batch.Items[0].OutputFileName;
        var renamed = batch with { Items = BatchPlanner.AssignOutputNames(batch.Items, t => t + ".mp3", n => string.Equals(n, occupied, StringComparison.Ordinal)) };
        Assert.NotEqual(occupied, renamed.Items[0].OutputFileName, StringComparer.Ordinal);
        Assert.Equal(batch.Items[0].SourceSha256, renamed.Items[0].SourceSha256);
        Assert.Equal(batch.Items[0].DocumentId, renamed.Items[0].DocumentId);
        Assert.Equal(batch.Items[0].RevisionId, renamed.Items[0].RevisionId);
        Assert.Equal("batch.authorized", Code(profile, consent, renamed));
        Assert.Equal("batch.consent-files-changed", Code(profile, consent, renamed.WithItem(renamed.Items[0] with { SourceSha256 = Sha("changed") })));
    }
}
