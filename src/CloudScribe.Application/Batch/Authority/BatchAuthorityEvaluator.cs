namespace CloudScribe.Application.Batch.Authority;

/// <summary>Pure check of whether a batch may spend. It never grants provider authority by itself: each file still
/// passes the existing article admission and financial checks before any paid request.</summary>
public static class BatchAuthorityEvaluator
{
    public static BatchAuthorityDecision Evaluate(BatchAuthorityProfile? profile, BatchConsent? consent, NarrationBatch batch,
        string accountStableId, string projectStableId, long otherBatchesCommittedMinorUnits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var setup = CheckProfile(profile, batch, accountStableId, projectStableId, now);
        if (setup is not null) return setup;
        if (consent is null)
            return profile!.Mode == BatchAuthorityMode.Reviewed
                ? BatchAuthorityDecision.Deny("batch.consent-missing-reviewed", "This batch has not been approved yet. Export the batch request and have it reviewed and installed.")
                : BatchAuthorityDecision.Deny("batch.consent-missing", "Approve this batch before starting it.");
        return CheckConsent(profile!, consent, batch, accountStableId, projectStableId, otherBatchesCommittedMinorUnits, now)
            ?? new(true, "batch.authorized", "Approved. Each file is still checked against its own approved maximum before it starts.");
    }

    /// <summary>Checks only the profile, for deciding whether the Approve button can be offered at all.</summary>
    public static BatchAuthorityDecision? CheckProfile(BatchAuthorityProfile? profile, NarrationBatch batch,
        string accountStableId, string projectStableId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!string.Equals(batch.Settings.Sha256(), batch.SettingsSha256, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-settings-changed", "The narration settings changed. Review the batch again.");
        if (batch.Settings.SkipUrls && !Documents.NarrationUrlOmission.IsSupportedVersion(batch.Settings.UrlTransformVersion)
            || !batch.Settings.SkipUrls && batch.Settings.UrlTransformVersion is not null)
            return BatchAuthorityDecision.Deny("batch.narration-version", "The URL skipping version is not supported. Prepare a new batch.");
        if (profile is null) return BatchAuthorityDecision.Deny("batch.authority-not-configured", "Batch narration is not set up on this computer. An administrator can install a batch authority profile.");
        if (profile.Problem() is { } problem) return BatchAuthorityDecision.Deny(problem, "The installed batch authority profile is not valid.");
        if (profile.Mode == BatchAuthorityMode.Off) return BatchAuthorityDecision.Deny("batch.authority-off", "Batch narration is turned off in the installed profile. You can still prepare and rehearse batches.");
        if (now >= profile.ExpiresUtc) return BatchAuthorityDecision.Deny("batch.authority-expired", "The installed batch authority profile has expired.");
        if (!string.Equals(profile.AccountStableId, accountStableId, StringComparison.Ordinal) || !string.Equals(profile.ProjectStableId, projectStableId, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.authority-account-mismatch", "The selected Google account or project is not the one the batch profile allows.");
        if (!profile.AllowsVoice(batch.Settings.VoiceStableId)) return BatchAuthorityDecision.Deny("batch.authority-voice", "The batch profile does not allow this voice.");
        if (!BatchEngines.Candidates(batch.Settings.Engine).Any(profile.AllowsEngine)) return BatchAuthorityDecision.Deny("batch.authority-engine", "The batch profile does not allow this narration engine.");
        if (batch.IncludedCount > profile.MaximumFilesPerBatch) return BatchAuthorityDecision.Deny("batch.authority-file-limit", $"The batch profile allows at most {profile.MaximumFilesPerBatch} files per batch.");
        return null;
    }

    private static BatchAuthorityDecision? CheckConsent(BatchAuthorityProfile profile, BatchConsent consent, NarrationBatch batch,
        string accountStableId, string projectStableId, long otherBatchesCommittedMinorUnits, DateTimeOffset now)
    {
        if (consent.SchemaVersion != BatchConsent.CurrentSchemaVersion || !string.Equals(consent.ComputeSha256(), consent.ConsentSha256, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-invalid", "The batch approval is damaged. Approve the batch again.");
        if (profile.Mode == BatchAuthorityMode.Reviewed && consent.Source != BatchConsentSource.ReviewedInstall)
            return BatchAuthorityDecision.Deny("batch.consent-needs-review", "This computer requires batches to be reviewed and installed; an in-app approval is not enough.");
        if (consent.BatchId != batch.BatchId || !string.Equals(consent.ProfileSha256, profile.Sha256(), StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-profile-changed", "The batch profile changed after approval. Approve the batch again.");
        if (!string.Equals(consent.SettingsSha256, batch.SettingsSha256, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-settings-changed", "The voice or settings changed after approval. Approve the batch again.");
        if (!string.Equals(consent.AccountStableId, accountStableId, StringComparison.Ordinal) || !string.Equals(consent.ProjectStableId, projectStableId, StringComparison.Ordinal))
            return BatchAuthorityDecision.Deny("batch.consent-account-mismatch", "The selected Google account or project differs from the approval.");
        if (!string.Equals(consent.Currency, profile.Currency, StringComparison.Ordinal) || consent.Scale != profile.Scale)
            return BatchAuthorityDecision.Deny("batch.consent-currency", "The approval uses a different currency from the profile.");
        if (now < consent.CreatedUtc || now >= consent.ExpiresUtc)
            return BatchAuthorityDecision.Deny("batch.consent-expired", "The batch approval has expired. Files already finished are kept; approve the rest again.");
        var included = batch.Included.Where(x => x.State != BatchItemState.Completed).ToArray();
        var byId = consent.Items.ToDictionary(x => x.ItemId);
        if (consent.Items.Count != byId.Count || consent.Items.Any(x => x.MaximumMinorUnits < 0))
            return BatchAuthorityDecision.Deny("batch.consent-invalid", "The batch approval is damaged. Approve the batch again.");
        foreach (var item in included)
        {
            if (!byId.TryGetValue(item.ItemId, out var approved) || !string.Equals(approved.SourceSha256, item.SourceSha256, StringComparison.Ordinal)
                || approved.DocumentId != item.DocumentId || approved.RevisionId != item.RevisionId
                || !string.Equals(approved.NarrationTextSha256, item.NarrationTextSha256, StringComparison.Ordinal)
                || approved.NarrationSource != item.NarrationSource)
                return BatchAuthorityDecision.Deny("batch.consent-files-changed", "The files in the batch changed after approval. Approve the batch again.");
            if (!profile.AllowsEngine(approved.Engine)
                || item.JobId is not null && item.Engine is { } chosen && !string.Equals(chosen, approved.Engine, StringComparison.Ordinal))
                return BatchAuthorityDecision.Deny("batch.consent-engine", "A file's narration engine is not allowed or differs from its approval.");
            bool longAudio = string.Equals(approved.Engine, BatchEngines.LongAudio, StringComparison.Ordinal);
            if (longAudio != approved.LongAudio is not null
                || longAudio && (!string.Equals(consent.Currency, "USD", StringComparison.Ordinal) || consent.Scale != 2
                    || approved.LongAudio!.CeilingUsdCents < 1 || approved.LongAudio.FamilyMaximumUsdCents < approved.LongAudio.CeilingUsdCents
                    || approved.MaximumMinorUnits != approved.LongAudio.FamilyMaximumUsdCents
                    || !string.Equals(approved.PlanSha256, approved.LongAudio.RequestBindingSha256, StringComparison.Ordinal)))
                return BatchAuthorityDecision.Deny("batch.consent-invalid", "The batch approval is damaged. Approve the batch again.");
        }
        long batchMaximum = consent.TotalMaximumMinorUnits;
        foreach (var completed in batch.Items.Where(x => x.State == BatchItemState.Completed && x.JobId is not null))
        {
            long alreadyApproved = byId.TryGetValue(completed.ItemId, out var approved) ? approved.MaximumMinorUnits : 0;
            batchMaximum = checked(batchMaximum + Math.Max(0, completed.ReservedMinorUnits - alreadyApproved));
        }
        if (batchMaximum > profile.PerBatchCeilingMinorUnits)
            return BatchAuthorityDecision.Deny("batch.ceiling-batch", "The approved maximum is above the per-batch limit.");
        if (checked(otherBatchesCommittedMinorUnits + batchMaximum) > profile.RollingCeilingMinorUnits)
            return BatchAuthorityDecision.Deny("batch.ceiling-rolling", $"This batch would go over the {profile.RollingWindowDays}-day limit, counting other batches' holds.");
        return null;
    }
}
