namespace CloudScribe.Application.Documents;

public sealed record NarrationLibraryPart(DocumentSummary Document, int Ordinal, bool ChangedSincePreparation);
