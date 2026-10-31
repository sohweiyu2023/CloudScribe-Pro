namespace CloudScribe.Application.Documents;

/// <summary>A bounded view of stored local items; page size is not a storage limit.</summary>
public sealed record LibraryPage<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int PageSize);
