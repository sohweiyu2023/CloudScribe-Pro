namespace CloudScribe.Application.Diagnostics;

public sealed record SupportBundlePreview(
    IReadOnlyList<SupportBundleFile> Files,
    long TotalSizeBytes,
    bool ContainsDocuments,
    bool ContainsAudio,
    bool ContainsSecrets,
    string Disclosure);
