using System.Net;

namespace CloudScribe.Infrastructure.Generation;

public sealed record GoogleHttpTransportResponse(HttpStatusCode StatusCode, ReadOnlyMemory<byte> Body, TimeSpan? RetryAfter);
