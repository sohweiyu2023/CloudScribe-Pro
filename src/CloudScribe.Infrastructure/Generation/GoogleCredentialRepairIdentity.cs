namespace CloudScribe.Infrastructure.Generation;

/// <summary>Declared identity plus the public key actually used by fresh authentication; not historical key provenance.</summary>
public sealed record GoogleCredentialRepairIdentity(string ProjectId, string ClientEmail, string PrivateKeyId, string PublicKeySha256);
