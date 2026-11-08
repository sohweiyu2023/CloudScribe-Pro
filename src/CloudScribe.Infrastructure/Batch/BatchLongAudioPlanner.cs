using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Batch;
using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Builds the exact Long Audio request for a batch file, offline, from the installed scope: same project,
/// credential reference, bucket, voice, language and encoding as every installed Long Audio job, and the scope's own
/// pricing formula for the request ceiling. Nothing is submitted or committed here.</summary>
public sealed class BatchLongAudioPlanner(InstalledGoogleLongAudioStore installed, IGoogleLongAudioScopeExtensionSource extension)
{
    /// <summary>Seconds of speech allowed per character when bounding the downloaded file (about 2× slow Chinese narration).</summary>
    private const decimal SecondsPerCharacterBound = 0.5m;
    private const int MaximumInputBytes = 1_000_000;

    public sealed record Context(InstalledGoogleLongAudioContext Installed, long HeadroomUsdCents, long CapUsdCents, string? Unsupported);

    /// <summary>Reads the installed scope and how much of its cap is still free for new batch approvals.</summary>
    public async Task<Context?> ReadAsync(NarrationBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var read = await installed.ReadInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (read.Context is not { } context) return null;
        var scope = context.Scope;
        string? unsupported = !string.Equals(scope.VoiceName, batch.Settings.VoiceStableId, StringComparison.Ordinal)
            || !string.Equals(scope.LanguageCode, batch.Settings.LanguageCode, StringComparison.Ordinal)
            ? $"Long Audio is set up for the voice {scope.VoiceName} ({scope.LanguageCode}), not this batch's voice."
            : null;
        var raw = ChirpPolicyFiles.Parse<GoogleLongAudioBudgetScope>(await ChirpPolicyFiles.ReadAsync(context.ScopePath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        var parent = ChirpPolicyFiles.Parse<ChirpArticleAdmissionPolicy>(await ChirpPolicyFiles.ReadAsync(context.ParentPolicyPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        long fixedCommitments = checked(parent.Budget.ProjectCeilingMinorUnits + raw.TaxFxProvisionUsdCents + GoogleLongAudioScopeExtension.InstalledApprovedUsdCents(raw));
        var current = await extension.LoadAsync(raw, context.ScopeSha256, context.LedgerPath, fixedCommitments, cancellationToken).ConfigureAwait(false);
        // This batch's own uncommitted approvals are replaced by a new approval; everything committed always counts.
        long others = current.Jobs.Where(x => x.BatchId != batch.BatchId || x.Committed).Sum(x => x.FamilyMaximumUsdCents);
        long headroom = raw.TotalGoogleCapUsdCents - fixedCommitments - others;
        return new(context, Math.Max(0, headroom), raw.TotalGoogleCapUsdCents, unsupported);
    }

    /// <summary>The exact Long Audio approval for one file's saved text, or why Long Audio cannot take it.</summary>
    public static BatchLongAudioPlan Plan(Context context, NarrationBatch batch, BatchItem item, string savedText)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(savedText);
        if (context.Unsupported is { } reason) return BatchLongAudioPlan.No(reason);
        var scope = context.Installed.Scope;
        int bytes = new UTF8Encoding(false, true).GetByteCount(savedText);
        if (bytes > MaximumInputBytes) return BatchLongAudioPlan.No("The text is larger than one Long Audio request allows (1 MB).");
        bool linear = string.Equals(scope.AudioEncoding, "LINEAR16", StringComparison.Ordinal);
        var attempt = Guid.NewGuid();
        string uri = $"gs://{scope.OutputBucket}/cloudscribe-batch/{batch.BatchId:N}/{attempt:N}{(linear ? ".wav" : ".mp3")}";
        GoogleLongAudioRequest request;
        try
        {
            request = GoogleLongAudioRequest.Create(attempt, scope.ProjectNumber, "global", scope.CredentialReferenceId, uri, savedText,
                scope.LanguageCode, scope.VoiceName, scope.AudioEncoding);
        }
        catch (ArgumentException) { return BatchLongAudioPlan.No("The installed Long Audio setup cannot express this file's request."); }
        long ceiling = Math.Max(1, checked((long)decimal.Ceiling(request.ConservativeCharacterUnits * scope.UsdPerMillionCharacters / 10_000m)));
        // A confirmed sentence-length failure may split the file into smaller requests covering the same text once more.
        long family = linear ? checked(ceiling * 2) : ceiling;
        var sample = scope.DocumentJobs is { Count: > 0 } installedJobs ? installedJobs[0] : null;
        int rate = sample?.ExpectedSampleRate ?? 24000, channels = sample?.ExpectedChannels ?? 1;
        long seconds = checked((long)decimal.Ceiling(item.Characters * SecondsPerCharacterBound) + 60);
        long maximumBytes = linear ? checked(seconds * rate * 2 * channels + 44) : checked(seconds * 16_000 + 4096);
        return new(new BatchLongAudioApproval(attempt, uri, request.BindingSha256, ceiling, family, maximumBytes, rate, channels, linear), null);
    }

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));
}
