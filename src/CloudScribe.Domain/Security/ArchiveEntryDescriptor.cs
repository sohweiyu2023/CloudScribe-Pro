namespace CloudScribe.Domain.Security;

public sealed record ArchiveEntryDescriptor(string RelativePath, long UncompressedLength, bool IsSymbolicLink);
