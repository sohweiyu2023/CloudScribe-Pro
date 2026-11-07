namespace CloudScribe.Infrastructure.Generation;

/// <summary>Exact independently reviewed installer input, not a proposal that approves itself.</summary>
public sealed record GoogleLongAudioBudgetScope(int Version, string ParentPolicySha256, string SourceApprovalSha256,
    string ProjectNumber, string CredentialReferenceId, string VoiceName, string LanguageCode, string AudioEncoding,
    string OutputBucket, long TotalGoogleCapUsdCents, long TaxFxProvisionUsdCents, decimal UsdPerMillionCharacters,
    string PricingProvenance, IReadOnlyDictionary<string, long> ApprovedRequestCeilingsUsdCents,
    IReadOnlyList<GoogleLongAudioDocumentJob>? DocumentJobs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AdaptiveRecoveryRootBindings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GoogleLongAudioFailedRangeReplacement>? FailedRangeReplacements = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GoogleLongAudioDeferredRange>? DeferredUnknownRanges = null);
