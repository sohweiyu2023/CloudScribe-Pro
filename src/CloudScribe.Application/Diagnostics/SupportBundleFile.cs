namespace CloudScribe.Application.Diagnostics;

public sealed record SupportBundleFile(string RelativePath, long SizeBytes, string Classification);
