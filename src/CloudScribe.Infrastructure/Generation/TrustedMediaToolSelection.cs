namespace CloudScribe.Infrastructure.Generation;

public sealed record TrustedMediaToolSelection(int Version, string EncoderPath, string EncoderSha256, string ProbeSha256);
