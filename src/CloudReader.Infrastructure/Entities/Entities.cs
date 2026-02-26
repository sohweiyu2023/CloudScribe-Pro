using CloudReader.Core.Enums;

namespace CloudReader.Infrastructure.Entities;

public sealed class Document
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DocumentMode Mode { get; set; }
    public string Language { get; set; } = "en-US";
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<DocumentTag> Tags { get; set; } = new List<DocumentTag>();
}

public sealed class Tag { public Guid Id { get; set; } public string Name { get; set; } = string.Empty; public ICollection<DocumentTag> Documents { get; set; } = new List<DocumentTag>(); }
public sealed class DocumentTag { public Guid DocumentId { get; set; } public Document Document { get; set; } = default!; public Guid TagId { get; set; } public Tag Tag { get; set; } = default!; }

public sealed class VoicePreset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string VoiceName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public double SpeakingRate { get; set; }
    public double Pitch { get; set; }
    public double VolumeGainDb { get; set; }
    public string AudioEncoding { get; set; } = "MP3";
    public int SampleRateHertz { get; set; }
    public string? EffectsProfileId { get; set; }
}

public sealed class Generation
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = default!;
    public DocumentMode Mode { get; set; }
    public GenerationStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Tier { get; set; } = string.Empty;
    public int CharCount { get; set; }
    public int ByteCount { get; set; }
    public decimal EstimatedCost { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
}

public sealed class Segment
{
    public Guid Id { get; set; }
    public Guid GenerationId { get; set; }
    public Generation Generation { get; set; } = default!;
    public int Idx { get; set; }
    public string? Speaker { get; set; }
    public Guid? VoicePresetId { get; set; }
    public VoicePreset? VoicePreset { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Utf8Bytes { get; set; }
    public string? AudioPath { get; set; }
    public int DurationMs { get; set; }
    public string Checksum { get; set; } = string.Empty;
}

public sealed class Lexicon
{
    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public string? LocaleOptional { get; set; }
    public string? Notes { get; set; }
}

public sealed class MonthlyUsage
{
    public Guid Id { get; set; }
    public string MonthKey { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public int TotalChars { get; set; }
    public int TotalGenerations { get; set; }
}
