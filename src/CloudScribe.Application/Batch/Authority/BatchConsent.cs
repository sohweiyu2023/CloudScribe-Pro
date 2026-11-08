using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Application.Batch.Authority;

/// <summary>Approval for one batch. It binds the exact files, plans, voice, per-file maxima, total and expiry,
/// so any change to the batch after approval needs a new approval.</summary>
public sealed record BatchConsent(
    int SchemaVersion,
    Guid ConsentId,
    Guid BatchId,
    BatchConsentSource Source,
    string ProfileSha256,
    string SettingsSha256,
    string AccountStableId,
    string ProjectStableId,
    string Currency,
    int Scale,
    IReadOnlyList<BatchConsentItem> Items,
    long TotalMaximumMinorUnits,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string ConsentSha256)
{
    public const int CurrentSchemaVersion = 2;

    public static BatchConsent Create(Guid consentId, NarrationBatch batch, BatchConsentSource source, BatchAuthorityProfile profile,
        string accountStableId, string projectStableId, IReadOnlyList<BatchConsentItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(items);
        long total = 0;
        foreach (var item in items) total = checked(total + item.MaximumMinorUnits);
        var consent = new BatchConsent(CurrentSchemaVersion, consentId, batch.BatchId, source, profile.Sha256(), batch.SettingsSha256,
            accountStableId, projectStableId, profile.Currency, profile.Scale, items, total, now, now + profile.ConsentLifetime, string.Empty);
        return consent with { ConsentSha256 = consent.ComputeSha256() };
    }

    public string ComputeSha256()
    {
        var text = new StringBuilder("batch-consent-v1\n");
        void Add(object? value) => text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        foreach (object? value in new object?[]
        {
            SchemaVersion, ConsentId, BatchId, Source, ProfileSha256, SettingsSha256, AccountStableId, ProjectStableId, Currency, Scale,
            TotalMaximumMinorUnits, CreatedUtc.UtcTicks, ExpiresUtc.UtcTicks, Items.Count,
        }) Add(value);
        foreach (var item in Items)
        {
            Add(string.Join('|', item.ItemId.ToString("N"), item.SourceSha256, item.DocumentId.ToString("N"), item.RevisionId.ToString("N"),
                item.PlanSha256, item.MaximumMinorUnits.ToString(CultureInfo.InvariantCulture), item.Engine));
            if (item.LongAudio is { } la)
                Add(string.Join('|', "long-audio", la.AttemptId.ToString("N"), la.OutputGcsUri, la.RequestBindingSha256,
                    la.CeilingUsdCents.ToString(CultureInfo.InvariantCulture), la.FamilyMaximumUsdCents.ToString(CultureInfo.InvariantCulture),
                    la.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture), la.ExpectedSampleRate.ToString(CultureInfo.InvariantCulture),
                    la.ExpectedChannels.ToString(CultureInfo.InvariantCulture), la.AdaptiveRecovery ? "split" : "whole"));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
