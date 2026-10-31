using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Never evaluates Message, ToString, Data, Source or arbitrary exception type names.</summary>
public static class SafeExceptionSummarizer
{
    public const int MaximumCauses = 4;
    public const int MaximumLocations = 8;

    public static SafeExceptionCategory Classify(Exception error) => error switch
    {
        OperationCanceledException => SafeExceptionCategory.Cancelled,
        TimeoutException => SafeExceptionCategory.Timeout,
        UnauthorizedAccessException or SecurityException => SafeExceptionCategory.AccessDenied,
        BadImageFormatException => SafeExceptionCategory.ImageFormat,
        TypeLoadException or DllNotFoundException or FileLoadException => SafeExceptionCategory.DependencyLoad,
        FileNotFoundException or DirectoryNotFoundException => SafeExceptionCategory.MissingFile,
        DbException => SafeExceptionCategory.Database,
        JsonException or XmlException => SafeExceptionCategory.MalformedData,
        InvalidDataException => SafeExceptionCategory.InvalidData,
        IOException => SafeExceptionCategory.Storage,
        HttpRequestException => SafeExceptionCategory.Network,
        COMException or Win32Exception => SafeExceptionCategory.NativeInterop,
        InvalidOperationException or ArgumentException or NotSupportedException => SafeExceptionCategory.Validation,
        _ => SafeExceptionCategory.Unknown,
    };

    public static SafeExceptionSummary? Capture(Exception? error)
    {
        if (error is null) return null;
        var locations = new List<SafeExceptionLocation>(MaximumLocations);
        var inner = new List<SafeExceptionCause>(MaximumCauses - 1);
        bool unavailable = false, truncated = false;
        Exception? current = error;
        int cause = 0;
        while (current is not null && cause < MaximumCauses)
        {
            if (cause != 0) inner.Add(new(Classify(current), current.HResult));
            CaptureLocations(current, cause, locations, ref unavailable, ref truncated);
            current = current.InnerException;
            cause++;
        }
        truncated |= current is not null || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 1;
        return new(1, Classify(error), error.HResult,
            error is Win32Exception native ? native.NativeErrorCode : null,
            error is SqliteException sqlite ? BoundedDatabaseCode(sqlite.SqliteErrorCode) : null,
            error is SqliteException extended ? BoundedDatabaseCode(extended.SqliteExtendedErrorCode) : null,
            locations.Count != 0 ? SafeExceptionStackState.Available : unavailable ? SafeExceptionStackState.Unavailable : SafeExceptionStackState.Missing,
            locations.ToArray(), inner.ToArray(), truncated, unavailable);
    }

    public static string? Describe(Exception? error) => error is null ? null : JsonSerializer.Serialize(Capture(error));

    private static int? BoundedDatabaseCode(int value) => value is >= 0 and <= 65_535 ? value : null;

    private static void CaptureLocations(Exception error, int cause, List<SafeExceptionLocation> output,
        ref bool unavailable, ref bool truncated)
    {
        try
        {
            var trace = new StackTrace(error, fNeedFileInfo: false);
            int limit = Math.Min(2, MaximumLocations - output.Count);
            truncated |= trace.FrameCount > limit;
            for (int index = 0; index < Math.Min(trace.FrameCount, limit); index++)
            {
                var frame = trace.GetFrame(index);
                var method = frame?.GetMethod();
                if (method is null) { unavailable = true; continue; }
                int token = method.MetadataToken;
                Guid module = method.Module.ModuleVersionId;
                if ((token & unchecked((int)0xff000000)) != 0x06000000 || module == Guid.Empty)
                { unavailable = true; continue; }
                output.Add(new(cause, module, token, Math.Max(-1, frame!.GetILOffset())));
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { unavailable = true; }
    }
}
