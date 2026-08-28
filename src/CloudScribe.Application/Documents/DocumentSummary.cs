using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Documents;

public sealed record DocumentSummary(
    Guid Id,
    string Title,
    long UpdatedAtUnixMilliseconds,
    DocumentStatus Status,
    bool IsFavorite,
    long ConcurrencyVersion);
