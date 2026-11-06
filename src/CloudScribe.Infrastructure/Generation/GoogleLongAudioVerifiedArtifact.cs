namespace CloudScribe.Infrastructure.Generation;

/// <summary>Verified local bytes and native decode, never an accepted provider response, article receipt or spend settlement.</summary>
public sealed record GoogleLongAudioVerifiedArtifact(GoogleLongAudioObjectIdentity Identity, string LocalPath,
    string AudioSha256, long Bytes)
{
    public override string ToString() => "Long Audio local artifact verified; not an accepted receipt.";
}
