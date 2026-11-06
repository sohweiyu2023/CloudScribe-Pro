namespace CloudScribe.Infrastructure.Generation;

/// <summary>Independently reviewed native binaries; the output boundary rehashes them before use.</summary>
public sealed record GoogleLongAudioMediaTools(string FfmpegPath, string FfmpegSha256, string FfprobePath, string FfprobeSha256);
