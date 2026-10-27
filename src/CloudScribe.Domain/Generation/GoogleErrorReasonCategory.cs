namespace CloudScribe.Domain.Generation;

public enum GoogleErrorReasonCategory
{
    NotProvided, Unknown, ServiceDisabled, BillingDisabled, ApiKeyInvalid,
    ApiKeyServiceBlocked, RateLimitExceeded, ResourceQuotaExceeded,
    ConsumerInvalid, AccessTokenExpired, AccessTokenScopeInsufficient,
}
