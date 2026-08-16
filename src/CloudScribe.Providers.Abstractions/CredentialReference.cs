namespace CloudScribe.Providers.Abstractions;

public sealed record CredentialReference
{
    public CredentialReference(string targetName)
    {
        TargetName = ProviderIdentifierRules.NormalizeStableId(targetName, nameof(targetName), maximumLength: 192);
    }

    public string TargetName { get; }
}
