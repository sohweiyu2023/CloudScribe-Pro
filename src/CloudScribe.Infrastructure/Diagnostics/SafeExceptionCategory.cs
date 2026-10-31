namespace CloudScribe.Infrastructure.Diagnostics;

public enum SafeExceptionCategory
{
    Unknown, Cancelled, Timeout, AccessDenied, MissingFile, Storage, Database, MalformedData,
    InvalidData, Validation, Network, DependencyLoad, ImageFormat, NativeInterop,
}
