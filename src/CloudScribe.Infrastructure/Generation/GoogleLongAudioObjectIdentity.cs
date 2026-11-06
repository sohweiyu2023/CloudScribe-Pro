namespace CloudScribe.Infrastructure.Generation;

/// <summary>Private immutable object identity pinned before its data is fetched.</summary>
public sealed record GoogleLongAudioObjectIdentity(string BindingSha256, string Bucket, string ObjectName,
    string Generation, long Bytes, string Md5Base64)
{
    public override string ToString() => "Long Audio object identity; private fields omitted.";
}
