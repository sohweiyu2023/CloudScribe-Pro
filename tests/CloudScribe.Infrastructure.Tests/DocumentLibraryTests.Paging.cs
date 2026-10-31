using CloudScribe.Application.Documents;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class DocumentLibraryTests
{
    [Theory]
    [InlineData(199)]
    [InlineData(200)]
    [InlineData(201)]
    public async Task PagingRetainsEveryStoredArticleAcrossTwoHundredBoundary(int count)
    {
        await using TestLibraryFixture fixture = await TestLibraryFixture.CreateAsync();
        var ids = new HashSet<Guid>();
        for (int index = 0; index < count; index++)
        {
            var document = await fixture.Library.CreateAsync($"Article {index:D3}", $"Body marker-{index:D3}", TestContext.Current.CancellationToken);
            ids.Add(document.Id);
        }

        LibraryPage<DocumentSummary> first = await fixture.Library.QueryPageAsync(string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        var last = await fixture.Library.QueryPageAsync(string.Empty, 200, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(count, first.TotalCount);
        Assert.Equal(Math.Min(count, 200), first.Items.Count);
        Assert.Equal(count > 200 ? 200 : 0, last.Offset);
        Assert.Equal(ids.Order(), first.Items.Concat(last.Items).Select(item => item.Id).Distinct().Order());
        var search = await fixture.Library.QueryPageAsync("marker-000", 200, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, search.TotalCount);
        Assert.Equal(0, search.Offset);
        Assert.Single(search.Items);
        foreach (Guid id in ids)
            Assert.NotNull(await fixture.Library.OpenAsync(id, TestContext.Current.CancellationToken));
    }
}
