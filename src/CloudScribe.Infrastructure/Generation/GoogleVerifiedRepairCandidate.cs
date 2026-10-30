namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleVerifiedRepairCandidate : IDisposable
{
    internal GoogleVerifiedRepairCandidate(GoogleCredentialRepairIdentity identity, GoogleRepairMemoryVault material, string reference)
    {
        Identity = identity;
        Material = material;
        Reference = reference;
    }

    public GoogleCredentialRepairIdentity Identity { get; }
    internal GoogleRepairMemoryVault Material { get; }
    internal string Reference { get; }
    public void Dispose() => Material.Dispose();
}
