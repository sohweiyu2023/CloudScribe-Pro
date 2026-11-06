using System.Globalization;
using System.Text.Json;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleLongAudioOutputBoundary
{
    private async Task<GoogleLongAudioObjectIdentity> LoadOrPinIdentityAsync(GoogleLongAudioRequest request,
        GoogleLongAudioOutputAllowance allowed, string prefix, CancellationToken token)
    {
        string path = prefix + ".object.json"; RequirePhysical(path);
        var (bucket, name) = ObjectParts(request);
        GoogleLongAudioObjectIdentity identity;
        if (File.Exists(path))
        {
            byte[] saved = await ReadBoundedFileAsync(path, 8192, token).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(saved, new JsonDocumentOptions { MaxDepth = 4 }); RequireUnique(parsed.RootElement);
            identity = JsonSerializer.Deserialize<GoogleLongAudioObjectIdentity>(saved) ?? throw new InvalidDataException("Object identity is missing.");
        }
        else
        {
            using var response = await GetAsync(request, ObjectUri(bucket, name, null, false), token).ConfigureAwait(false);
            byte[] metadata = await ReadBoundedResponseAsync(response.Content, 16_384, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 8 }); RequireUnique(json.RootElement);
            var r = json.RootElement;
            identity = new(request.BindingSha256, r.GetProperty("bucket").GetString()!, r.GetProperty("name").GetString()!,
                r.GetProperty("generation").GetString()!, long.Parse(r.GetProperty("size").GetString()!, NumberStyles.None, CultureInfo.InvariantCulture),
                r.GetProperty("md5Hash").GetString()!);
            RequireIdentity(identity, request, allowed, bucket, name);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(JsonSerializer.SerializeToUtf8Bytes(identity)); file.Flush(flushToDisk: true); }
                RequirePhysical(path);
                File.Move(pending, path, overwrite: false);
            }
            finally { if (File.Exists(pending)) { RequirePhysical(pending); File.Delete(pending); } }
        }
        RequireIdentity(identity, request, allowed, bucket, name);
        return identity;
    }

    private static void RequireIdentity(GoogleLongAudioObjectIdentity identity, GoogleLongAudioRequest request,
        GoogleLongAudioOutputAllowance allowed, string bucket, string name)
    {
        if (!string.Equals(identity.BindingSha256, request.BindingSha256, StringComparison.Ordinal)
            || !string.Equals(identity.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(identity.ObjectName, name, StringComparison.Ordinal)
            || !long.TryParse(identity.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation <= 0
            || !string.Equals(identity.Generation, generation.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || identity.Bytes < 12 || identity.Bytes > allowed.MaximumBytes || identity.Md5Base64.Length != 24
            || Convert.FromBase64String(identity.Md5Base64).Length != 16)
            throw new InvalidDataException("Object identity, size or checksum differs from the approved private output.");
    }

    private static void RequireCompletedBinding(GoogleLongAudioRequest request, GoogleLongAudioCheckpoint checkpoint)
    {
        string operationPrefix = "projects/" + request.ProjectNumber + "/locations/" + request.Location + "/operations/";
        if (checkpoint.State != GoogleLongAudioState.CompletedUnverified || checkpoint.HttpStatus != 200 || checkpoint.RpcCode is not null || checkpoint.OperationParseFailed
            || checkpoint.OperationName is null || !checkpoint.OperationName.StartsWith(operationPrefix, StringComparison.Ordinal)
            || checkpoint.OperationName.Length <= operationPrefix.Length
            || !string.Equals(checkpoint.BindingSha256, request.BindingSha256, StringComparison.Ordinal)
            || !string.Equals(checkpoint.OutputGcsUri, request.OutputGcsUri, StringComparison.Ordinal)
            || !string.Equals(checkpoint.CredentialReferenceId, request.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidOperationException("An exact completed operation is required; no new synthesis is performed here.");
    }

    private void RequireAllowance(GoogleLongAudioRequest request, GoogleLongAudioOutputAllowance allowed)
    {
        string expected = request.OutputGcsUri.EndsWith(".mp3", StringComparison.Ordinal) ? "mp3" : "pcm_s16le";
        if (allowed.MaximumBytes < 12 || (allowed.ReadNotAfterUtc is { } deadline && deadline <= clock.GetUtcNow()) || allowed.SampleRate <= 0 || allowed.Channels <= 0
            || !string.Equals(expected, allowed.ExpectedCodec, StringComparison.Ordinal))
            throw new InvalidOperationException("Exact unexpired storage-read and decode authority is required.");
    }

    private static (string Bucket, string Name) ObjectParts(GoogleLongAudioRequest request)
    {
        int slash = request.OutputGcsUri.IndexOf('/', 5);
        return (request.OutputGcsUri[5..slash], request.OutputGcsUri[(slash + 1)..]);
    }

    private static Uri ObjectUri(string bucket, string name, string? generation, bool media) => new(
        "https://storage.googleapis.com/storage/v1/b/" + Uri.EscapeDataString(bucket) + "/o/" + Uri.EscapeDataString(name)
        + (generation is null ? "?fields=bucket,name,generation,size,md5Hash" : "?generation=" + generation + "&ifGenerationMatch=" + generation + (media ? "&alt=media" : "")));
}
