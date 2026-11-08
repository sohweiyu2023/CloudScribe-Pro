using System.Text;
using CloudScribe.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationCopyStoreTests
{
    [Fact]
    public async Task PreparedCopyCanBeVerifiedUsingReadOnlyDatabaseAccess()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SourceAsync();
        var prepared = await PrepareAsync(fixture.Store(), source);
        var options = new DbContextOptionsBuilder<CloudScribeDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString).Options;
        var readOnly = new EfNarrationCopyStore(new Factory(options), new(fixture.Paths), TimeProvider.System);
        Assert.Equal(prepared, await readOnly.FindAsync(prepared.Narration.Id, Token));
        Assert.Null(await readOnly.FindAsync(source.Id, Token));
    }

    [Fact]
    public async Task LongCopyTitleNeverSplitsASupplementaryUnicodeCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        string title = new string('a', 211) + char.ConvertFromUtf32(0x1F600) + new string('b', 27);
        var source = await fixture.Library.CreateAsync(title, SourceText, Token);
        var prepared = await PrepareAsync(fixture.Store(), source);
        Assert.StartsWith(new string('a', 211) + " (narration", prepared.Narration.Title, StringComparison.Ordinal);
        Assert.True(prepared.Narration.Title.Length <= 240);
        var utf8 = new UTF8Encoding(false, true);
        Assert.Equal(prepared.Narration.Title, utf8.GetString(utf8.GetBytes(prepared.Narration.Title)));
        Assert.Equal(source, await fixture.Library.OpenAsync(source.Id, Token));
    }
}
