namespace CloudScribe.Domain.Generation;

public enum CompletionProtocolExceptionType
{
    Other, ValueError, TypeError, KeyError, IndexError, AttributeError, AssertionError, JSONDecodeError,
    UnicodeDecodeError, UnicodeEncodeError, MemoryError, OSError, PermissionError, FileNotFoundError, TimeoutError, OperationalError,
    DatabaseError, IntegrityError, EOFError, OverflowError, RuntimeError, Rejected,
}
