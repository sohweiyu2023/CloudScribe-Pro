using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CloudScribe.Application.Batch.Authority;

/// <summary>Administrator settings for batch narration, installed once into the read-only install folder.
/// Amounts are in the provider's billing currency (<see cref="Currency"/>, minor units at <see cref="Scale"/>);
/// <see cref="DisplayRate"/> converts them to <see cref="DisplayCurrency"/> for display only, never for limits.</summary>
public sealed record BatchAuthorityProfile(
    int SchemaVersion,
    BatchAuthorityMode Mode,
    string ProfileRootSha256,
    string AccountStableId,
    string ProjectStableId,
    IReadOnlyList<string> AllowedVoiceStableIds,
    IReadOnlyList<string> AllowedEngines,
    BatchFailurePolicy FailurePolicy,
    string Currency,
    int Scale,
    long PerBatchCeilingMinorUnits,
    long RollingCeilingMinorUnits,
    int RollingWindowDays,
    int MaximumFilesPerBatch,
    TimeSpan ConsentLifetime,
    DateTimeOffset ExpiresUtc,
    string DisplayCurrency,
    decimal DisplayRate,
    long EstimateMinorUnitsPerMillionCharacters)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Checks internal consistency. Returns null when valid, otherwise a short reason code.</summary>
    public string? Problem()
    {
        if (SchemaVersion != CurrentSchemaVersion) return "batch-authority.schema";
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(FailurePolicy)) return "batch-authority.mode";
        if (!IsHash(ProfileRootSha256)) return "batch-authority.profile-root";
        if (string.IsNullOrWhiteSpace(AccountStableId) || string.IsNullOrWhiteSpace(ProjectStableId)) return "batch-authority.account";
        if (AllowedVoiceStableIds is null || AllowedVoiceStableIds.Any(string.IsNullOrWhiteSpace)) return "batch-authority.voices";
        if (AllowedEngines is null || AllowedEngines.Count == 0 || AllowedEngines.Any(x => !BatchEngines.IsFileEngine(x))) return "batch-authority.engine";
        if (string.IsNullOrWhiteSpace(Currency) || Scale is < 0 or > 6) return "batch-authority.currency";
        if (PerBatchCeilingMinorUnits < 1 || RollingCeilingMinorUnits < PerBatchCeilingMinorUnits) return "batch-authority.ceilings";
        if (RollingWindowDays is < 1 or > 366) return "batch-authority.window";
        if (MaximumFilesPerBatch is < 1 or > 10_000) return "batch-authority.file-limit";
        if (ConsentLifetime <= TimeSpan.Zero || ConsentLifetime > TimeSpan.FromDays(7)) return "batch-authority.consent-lifetime";
        if (string.IsNullOrWhiteSpace(DisplayCurrency) || DisplayRate <= 0) return "batch-authority.display";
        if (EstimateMinorUnitsPerMillionCharacters < 1) return "batch-authority.estimate-rate";
        return null;
    }

    /// <summary>Pre-flight estimate with a 10% margin. The binding per-file maximum comes from the article review before each file.</summary>
    public long EstimateMinorUnits(int characters) =>
        characters <= 0 ? 0 : checked((long)Math.Ceiling(characters * (decimal)EstimateMinorUnitsPerMillionCharacters * 1.10m / 1_000_000m));

    public bool AllowsEngine(string engine) => AllowedEngines.Contains(engine, StringComparer.Ordinal);

    public bool AllowsVoice(string voiceStableId) =>
        AllowedVoiceStableIds.Count == 0 || AllowedVoiceStableIds.Contains(voiceStableId, StringComparer.Ordinal);

    public string Sha256()
    {
        var text = new StringBuilder("batch-authority-profile-v1\n");
        foreach (object? value in new object?[]
        {
            SchemaVersion, Mode, ProfileRootSha256, AccountStableId, ProjectStableId, string.Join('\u001f', AllowedVoiceStableIds), string.Join('\u001f', AllowedEngines),
            FailurePolicy, Currency, Scale, PerBatchCeilingMinorUnits, RollingCeilingMinorUnits, RollingWindowDays, MaximumFilesPerBatch,
            ConsentLifetime.Ticks, ExpiresUtc.UtcTicks, DisplayCurrency, DisplayRate, EstimateMinorUnitsPerMillionCharacters,
        })
            text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
