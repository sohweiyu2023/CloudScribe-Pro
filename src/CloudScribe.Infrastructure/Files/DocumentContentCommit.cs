namespace CloudScribe.Infrastructure.Files;

public sealed record DocumentContentCommit(string RelativePath, string Sha256, long ByteLength);
