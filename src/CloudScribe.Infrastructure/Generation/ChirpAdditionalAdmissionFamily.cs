namespace CloudScribe.Infrastructure.Generation;

/// <summary>One allowance shared by every plan in this family. Provision is charged before any request is reserved.</summary>
public sealed record ChirpAdditionalAdmissionFamily(Guid FamilyId, ChirpArticleAdmissionPolicy Policy,
    long AllInCeilingMinorUnits, long ProvisionMinorUnits, string ReviewedBaselineSha256,
    IReadOnlyList<ChirpAdditionalRequestPin> Requests);
