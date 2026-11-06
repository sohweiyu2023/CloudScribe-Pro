using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Infrastructure.Generation;

public static class GoogleLongAudioDocumentCompiler
{
    public static GoogleLongAudioRequest Compile(GoogleLongAudioBudgetScope scope, GoogleLongAudioDocumentJob job, string savedText)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(savedText);
        if (job.DocumentId == Guid.Empty || job.RevisionId == Guid.Empty || job.AttemptId == Guid.Empty
            || !string.Equals(job.InputMode, "text", StringComparison.Ordinal)
            || !string.Equals(job.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(savedText))), StringComparison.Ordinal)
            || job.MaximumOutputBytes < 12 || job.ExpectedSampleRate <= 0 || job.ExpectedChannels <= 0)
            throw new InvalidDataException("The saved Long Audio source or configured output settings changed.");
        var request = GoogleLongAudioRequest.Create(job.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            job.OutputGcsUri, savedText, scope.LanguageCode, scope.VoiceName, scope.AudioEncoding);
        if (scope.ApprovedRequestCeilingsUsdCents is null || !scope.ApprovedRequestCeilingsUsdCents.ContainsKey(request.BindingSha256))
            throw new InvalidDataException("The exact saved Long Audio payload is outside its installed scope.");
        return request;
    }

    public static GoogleLongAudioOutputAllowance OutputAllowance(GoogleLongAudioBudgetScope scope, GoogleLongAudioDocumentJob job) =>
        new(job.MaximumOutputBytes, null, string.Equals(scope.AudioEncoding, "MP3", StringComparison.Ordinal) ? "mp3" : "pcm_s16le",
            job.ExpectedSampleRate, job.ExpectedChannels);
}
