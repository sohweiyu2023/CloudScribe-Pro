using CloudScribe.Application.Batch.Authority;

namespace CloudScribe.Infrastructure.Batch;

/// <summary>Everything needed to approve a whole batch at once: each file's engine, exact plan or Long Audio request,
/// maximum, and the approval to install or save. It holds hashes and structure only, never article text.
/// <see cref="LongAudioHeadroomUsdCents"/> is the room left in the installed Long Audio cap for new approvals.</summary>
public sealed record BatchAdmissionRequest(
    string Format,
    Guid BatchId,
    string ProfileSha256,
    string SettingsSha256,
    string AccountStableId,
    string ProjectStableId,
    string LanguageCode,
    string VoiceName,
    string Currency,
    int Scale,
    IReadOnlyList<BatchAdmissionRequestItem> Items,
    long TotalEstimatedMaximumMinorUnits,
    BatchConsent ProposedConsent,
    long? LongAudioHeadroomUsdCents = null,
    long? LongAudioCapUsdCents = null)
{
    public const string CurrentFormat = "cloudscribe-batch-admission-request-v2";

    public long LongAudioMaximumUsdCents => Items.Where(x => x.LongAudio is not null).Sum(x => x.LongAudio!.FamilyMaximumUsdCents);

    public bool LongAudioFits => LongAudioHeadroomUsdCents is not { } room || LongAudioMaximumUsdCents <= room;

    public IEnumerable<BatchAdmissionRequestItem> Unnarratable => Items.Where(x => x.Engine is null);

    public IEnumerable<BatchAdmissionRequestItem> NeedingArticleAdmission => Items.Where(x => x.NeedsArticleAdmission);
}
