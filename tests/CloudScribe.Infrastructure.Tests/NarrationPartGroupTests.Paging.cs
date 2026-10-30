namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    [Fact]
    public async Task PagingCountsRootArticlesAndSearchesChildrenBeyondFirstPage()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var batch = await fixture.CreateBatchAsync("Grouped original", 2);
        await fixture.Groups.RegisterAsync(batch.Preparation.GroupId, batch.Members, TestContext.Current.CancellationToken);
        for (int index = 0; index < 200; index++)
            await fixture.Library.CreateAsync($"Ordinary {index:D3}", "Synthetic text", TestContext.Current.CancellationToken);

        var first = await fixture.Groups.QueryPageAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken);
        var second = await fixture.Groups.QueryPageAsync(string.Empty, 200, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(201, first.TotalCount);
        Assert.Equal(200, first.Items.Count);
        var last = Assert.Single(second.Items);
        Assert.Equal(batch.Parent.Id, last.Document.Id);
        Assert.Equal(2, last.Parts.Count);
        Assert.Equal(201, first.Items.Concat(second.Items).Select(item => item.Document.Id).Distinct().Count());
        var search = await fixture.Groups.QueryPageAsync("marker-01", 200, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, search.Offset);
        Assert.Equal(batch.Parent.Id, Assert.Single(search.Items).Document.Id);
        Assert.Equal(2, Assert.Single(search.Items).Parts.Count);
    }
}
