using System.Text.Json;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>One separately approved duplicate-risk request. The original remains unknown and held.</summary>
public sealed record GoogleLongAudioExplicitRegeneration(Guid AttemptId, string BindingSha256,
    string OutputGcsUri, long MaximumUsdCents)
{
    public const string PlannerVersion = "long-audio-explicit-unknown-regeneration-v1";

    internal static GoogleLongAudioRequest? Resolve(string source, ChirpNarrationRange range,
        GoogleLongAudioRequest root, GoogleLongAudioRequest original, GoogleLongAudioCheckpoint checkpoint,
        GoogleLongAudioBudgetScope scope, string operations)
    {
        var descriptor = scope.DeferredUnknownRanges?.SingleOrDefault(x => x.AttemptId == original.AttemptId);
        if (descriptor?.ExplicitRegeneration is not { } approval) return null;
        GoogleLongAudioDeferredRange.RequireScope(scope);
        string path = Path.Combine(operations, original.AttemptId.ToString("N") + ".json");
        ChirpPolicyFiles.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 8192) throw new InvalidDataException("The retained unknown checkpoint extent changed.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (!string.Equals(ChirpPolicyFiles.Hash(bytes), descriptor.CheckpointSha256, StringComparison.Ordinal)
            || new GoogleLongAudioFileJournal(operations).Load(original.AttemptId) != checkpoint
            || checkpoint.State != GoogleLongAudioState.PostClaimed || checkpoint.OperationName is not null
            || checkpoint.OperationParseFailed || checkpoint.HttpStatus != 0 || checkpoint.RpcCode is not null
            || checkpoint.Observation is not { ReadbackVerified: true }
            || checkpoint.Observation.ReadState == CloudScribe.Domain.Generation.GoogleResponseReadState.Complete
            || checkpoint.AttemptId != original.AttemptId || original.AttemptId == root.AttemptId
            || !string.Equals(descriptor.RootBindingSha256, root.BindingSha256, StringComparison.Ordinal)
            || range.StartScalar != descriptor.StartScalar || range.EndScalarExclusive != descriptor.EndScalarExclusive
            || range.ReusedReceiptSha256 is not null
            || !string.Equals(checkpoint.BindingSha256, original.BindingSha256, StringComparison.Ordinal)
            || !string.Equals(checkpoint.PayloadSha256, original.PayloadSha256, StringComparison.Ordinal)
            || checkpoint.PayloadBytes != original.PayloadBytes
            || !string.Equals(checkpoint.OutputGcsUri, original.OutputGcsUri, StringComparison.Ordinal))
            throw new InvalidDataException("The explicit regeneration no longer matches its retained unknown request.");
        string text = ChirpNarrationPlanner.Slice(source, range);
        using var wire = JsonDocument.Parse(original.CopyPayload());
        string folder = root.OutputGcsUri[..(root.OutputGcsUri.LastIndexOf('/') + 1)];
        if (!string.Equals(wire.RootElement.GetProperty("input").GetProperty("text").GetString(), text, StringComparison.Ordinal)
            || !string.Equals(approval.OutputGcsUri, folder + approval.AttemptId.ToString("N") + ".wav", StringComparison.Ordinal)
            || !string.Equals(root.AudioEncoding, "LINEAR16", StringComparison.Ordinal)
            || !string.Equals(original.AudioEncoding, root.AudioEncoding, StringComparison.Ordinal)
            || !string.Equals(original.VoiceName, root.VoiceName, StringComparison.Ordinal)
            || !string.Equals(original.LanguageCode, root.LanguageCode, StringComparison.Ordinal)
            || !string.Equals(original.ProjectNumber, root.ProjectNumber, StringComparison.Ordinal)
            || !string.Equals(original.CredentialReferenceId, root.CredentialReferenceId, StringComparison.Ordinal))
            throw new InvalidDataException("The explicit regeneration source, voice or output identity changed.");
        var request = GoogleLongAudioRequest.Create(approval.AttemptId, root.ProjectNumber, root.Location,
            root.CredentialReferenceId, approval.OutputGcsUri, text, root.LanguageCode, root.VoiceName, "LINEAR16");
        long amount = Math.Max(1, checked((long)decimal.Ceiling(request.ConservativeCharacterUnits * scope.UsdPerMillionCharacters / 10_000m)));
        if (!string.Equals(request.BindingSha256, approval.BindingSha256, StringComparison.Ordinal)
            || string.Equals(request.BindingSha256, original.BindingSha256, StringComparison.Ordinal)
            || amount > approval.MaximumUsdCents)
            throw new InvalidDataException("The exact regeneration request or cost is outside its approval.");
        return request;
    }
}
