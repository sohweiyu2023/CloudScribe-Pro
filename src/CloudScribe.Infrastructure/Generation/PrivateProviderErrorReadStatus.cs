namespace CloudScribe.Infrastructure.Generation;

/// <summary>Safe private-store health, separate from private record contents. Empty is not proof of successful access.</summary>
public enum PrivateProviderErrorReadStatus { NotRead, Success, CorruptOrUnprotectable, AccessDenied, Unavailable, Failed, Disposed }
