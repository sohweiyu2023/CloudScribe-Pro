using System.Net;
using CloudScribe.Domain.Generation;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleHttpTransportResponse(HttpStatusCode StatusCode, ReadOnlyMemory<byte> Body, TimeSpan? RetryAfter,
    GoogleErrorStatus ErrorStatus = GoogleErrorStatus.NotProvided);
