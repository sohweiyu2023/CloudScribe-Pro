namespace CloudScribe.Application.Documents;

public sealed record NarrationLibraryArticle(DocumentSummary Document, IReadOnlyList<NarrationLibraryPart> Parts, bool ChangedSincePreparation);
