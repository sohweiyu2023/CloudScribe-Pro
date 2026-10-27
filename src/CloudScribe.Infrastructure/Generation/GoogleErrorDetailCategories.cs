using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleErrorDetailCategories(GoogleErrorReasonCategory Reason,
    GoogleErrorDomainCategory Domain, GoogleErrorFieldCategory Field)
{
    public static GoogleErrorDetailCategories Missing { get; } = new(GoogleErrorReasonCategory.NotProvided,
        GoogleErrorDomainCategory.NotProvided, GoogleErrorFieldCategory.NotProvided);
    public static GoogleErrorDetailCategories Unknown { get; } = new(GoogleErrorReasonCategory.Unknown,
        GoogleErrorDomainCategory.Unknown, GoogleErrorFieldCategory.Unknown);
}
