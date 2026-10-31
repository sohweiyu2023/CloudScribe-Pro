namespace CloudScribe.Application.Documents;

public interface ILibraryPageSource<T>
{
    Task<LibraryPage<T>> QueryPageAsync(string query, int offset = 0, int limit = 200,
        CancellationToken cancellationToken = default);
}
