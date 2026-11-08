namespace CloudScribe.Infrastructure.Batch;

/// <summary><c>pins.json</c>, written by <c>Install-BatchAuthority.ps1</c>: the SHA-256 of the installed profile and of each
/// installed consent (keyed by batch id in "N" format).</summary>
public sealed record BatchAuthorityPins(int SchemaVersion, string ProfileSha256, IReadOnlyDictionary<string, string>? Consents);
