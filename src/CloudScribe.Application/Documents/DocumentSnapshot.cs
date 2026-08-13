using CloudScribe.Domain.Documents;

namespace CloudScribe.Application.Documents;

public sealed record DocumentSnapshot(
    Guid Id,
    string Title,
    string Text,
    long CreatedAtUnixMilliseconds,
    long UpdatedAtUnixMilliseconds,
    DocumentStatus Status,
    bool IsFavorite,
    Guid? CurrentRevisionId,
    string? VoiceReference,
    string? PresetReference,
    long ConcurrencyVersion);
