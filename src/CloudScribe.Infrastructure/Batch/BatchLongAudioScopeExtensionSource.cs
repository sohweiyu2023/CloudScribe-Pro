using CloudScribe.Application.Batch.Authority;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Turns batch approvals into Long Audio jobs for the installed scope. Rules:
/// <list type="bullet">
/// <item>A job that already holds a ledger commitment is always kept, whatever happened to its profile or approval since,
/// so its saved operation can still be checked, downloaded and reconciled.</item>
/// <item>A job without a commitment needs a current, valid approval: matching profile, not expired, allowed mode
/// (an in-app approval only in <see cref="BatchAuthorityMode.InAppWithinLimits"/>), Long Audio allowed, within the per-batch ceiling.</item>
/// <item>Every job must fit the installed scope exactly (bucket, encoding, unique document/attempt/binding) and the
/// installed total cap with room for its whole family (root plus any splits); jobs that do not fit are left out in
/// approval order, so a later approval can never displace an earlier one or an installed job.</item>
/// </list>
/// It never throws; any problem means fewer (or no) additions.</summary>
public sealed class BatchLongAudioScopeExtensionSource(InstalledBatchAuthorityStore authority, TimeProvider clock) : IGoogleLongAudioScopeExtensionSource
{
    public async ValueTask<GoogleLongAudioScopeExtension> LoadAsync(GoogleLongAudioBudgetScope scope, string scopeSha256, string ledgerPath,
        long fixedCommitmentsUsdCents, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(scope);
            var consents = await authority.ListConsentsAsync(cancellationToken).ConfigureAwait(false);
            if (consents.Count == 0) return GoogleLongAudioScopeExtension.Empty;
            var profile = await authority.LoadProfileAsync(cancellationToken).ConfigureAwait(false);
            var claimed = ReadClaimedBindings(ledgerPath, scopeSha256);
            var now = clock.GetUtcNow();
            var candidates = consents
                .Where(c => c.SchemaVersion == BatchConsent.CurrentSchemaVersion && string.Equals(c.ComputeSha256(), c.ConsentSha256, StringComparison.Ordinal)
                    && string.Equals(c.Currency, "USD", StringComparison.Ordinal) && c.Scale == 2)
                .OrderBy(c => c.CreatedUtc).ThenBy(c => c.ConsentId)
                .SelectMany(c => c.Items.Where(i => i.LongAudio is not null && string.Equals(i.Engine, BatchEngines.LongAudio, StringComparison.Ordinal)).Select(i => (Consent: c, Item: i)))
                .ToArray();
            var jobs = new List<GoogleLongAudioBatchJob>();
            long used = 0;
            // Committed jobs first: they can never be dropped. Then uncommitted ones, in approval order, while they fit.
            foreach (bool committedPass in new[] { true, false })
            {
                foreach (var (consent, item) in candidates)
                {
                    var la = item.LongAudio!;
                    bool committed = claimed.Contains(la.RequestBindingSha256);
                    if (committed != committedPass || !Fits(scope, la) || Collides(scope, jobs, item, la)) continue;
                    if (!committed && !Current(profile, consent, now)) continue;
                    long next = checked(used + la.FamilyMaximumUsdCents);
                    if (!committed && checked(fixedCommitmentsUsdCents + next) > scope.TotalGoogleCapUsdCents) continue;
                    used = next;
                    jobs.Add(new(consent.BatchId, item.ItemId, item.DocumentId, item.RevisionId, item.SourceSha256, la.AttemptId, la.OutputGcsUri,
                        la.MaximumOutputBytes, la.ExpectedSampleRate, la.ExpectedChannels, la.RequestBindingSha256, la.CeilingUsdCents,
                        la.FamilyMaximumUsdCents, la.AdaptiveRecovery, committed));
                }
            }
            return new(jobs);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not OperationCanceledException)
        { return GoogleLongAudioScopeExtension.Empty; }
    }

    /// <summary>Room left in the installed cap for new batch approvals, after installed approvals and current batch jobs.</summary>
    public static long Headroom(GoogleLongAudioBudgetScope effectiveScope, long projectCeilingUsdCents)
    {
        ArgumentNullException.ThrowIfNull(effectiveScope);
        long installedAndBatch = GoogleLongAudioScopeExtension.InstalledApprovedUsdCents(effectiveScope);
        return effectiveScope.TotalGoogleCapUsdCents - projectCeilingUsdCents - effectiveScope.TaxFxProvisionUsdCents - installedAndBatch;
    }

    private static bool Current(BatchAuthorityProfile? profile, BatchConsent consent, DateTimeOffset now) =>
        profile is not null && profile.Problem() is null && now < profile.ExpiresUtc && profile.Mode != BatchAuthorityMode.Off
        && profile.AllowsEngine(BatchEngines.LongAudio)
        && (consent.Source == BatchConsentSource.ReviewedInstall || profile.Mode == BatchAuthorityMode.InAppWithinLimits)
        && string.Equals(consent.ProfileSha256, profile.Sha256(), StringComparison.Ordinal)
        && now >= consent.CreatedUtc && now < consent.ExpiresUtc
        && consent.TotalMaximumMinorUnits <= profile.PerBatchCeilingMinorUnits;

    private static bool Fits(GoogleLongAudioBudgetScope scope, BatchLongAudioApproval la)
    {
        string extension = string.Equals(scope.AudioEncoding, "MP3", StringComparison.Ordinal) ? ".mp3" : ".wav";
        return la.AttemptId != Guid.Empty && la.CeilingUsdCents > 0 && la.FamilyMaximumUsdCents >= la.CeilingUsdCents
            && la.MaximumOutputBytes >= 12 && la.ExpectedSampleRate > 0 && la.ExpectedChannels > 0
            && la.OutputGcsUri.StartsWith("gs://" + scope.OutputBucket + "/", StringComparison.Ordinal)
            && la.OutputGcsUri.EndsWith("/" + la.AttemptId.ToString("N") + extension, StringComparison.Ordinal)
            && (!la.AdaptiveRecovery || string.Equals(scope.AudioEncoding, "LINEAR16", StringComparison.Ordinal))
            && la.RequestBindingSha256 is { Length: 64 } && la.RequestBindingSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool Collides(GoogleLongAudioBudgetScope scope, List<GoogleLongAudioBatchJob> jobs, BatchConsentItem item, BatchLongAudioApproval la) =>
        scope.DocumentJobs?.Any(x => x.DocumentId == item.DocumentId || x.AttemptId == la.AttemptId) == true
        || scope.ApprovedRequestCeilingsUsdCents?.ContainsKey(la.RequestBindingSha256) == true
        || jobs.Any(x => x.DocumentId == item.DocumentId || x.AttemptId == la.AttemptId
            || string.Equals(x.RequestBindingSha256, la.RequestBindingSha256, StringComparison.Ordinal));

    private static HashSet<string> ReadClaimedBindings(string ledgerPath, string scopeSha256)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(ledgerPath)) return claimed;
        var ledger = System.Text.Json.JsonSerializer.Deserialize<GoogleLongAudioBudgetLedger>(File.ReadAllBytes(ledgerPath));
        if (ledger is null || ledger.Version != 1 || !string.Equals(ledger.ScopeSha256, scopeSha256, StringComparison.Ordinal) || ledger.Claims is null) return claimed;
        foreach (var claim in ledger.Claims) { claimed.Add(claim.RequestBindingSha256); if (claim.Recovery is { } r) claimed.Add(r.RootBindingSha256); }
        return claimed;
    }
}
