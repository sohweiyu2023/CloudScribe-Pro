namespace CloudScribe.App.Composition;

public sealed record GoogleGenerationAcceptedMp3Output(
    string Path,
    int ByteCount,
    string Sha256);
