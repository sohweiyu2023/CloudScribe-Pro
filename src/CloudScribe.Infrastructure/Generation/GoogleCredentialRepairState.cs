using CloudScribe.Application.Providers;

namespace CloudScribe.Infrastructure.Generation;

internal sealed record GoogleCredentialRepairState(ProviderAccountSnapshot Account, string ProjectId,
    string FinancialSha256, GoogleCredentialRepairJournal? Active);
