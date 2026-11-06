using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

internal static class GoogleLongAudioTransportLog
{
    internal static void Write(ILogger? logger, GoogleLongAudioRequest request, string correlation, LongAudioEndpoint endpoint,
        int status, GoogleResponseReadState? readState, int bytes, long duration, Guid? record, int? failure, HttpResponseHeaders? headers,
        bool sendStarted, bool callerCancelled)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Information)) return;
        bool post = endpoint == LongAudioEndpoint.SynthesizeV1;
        CloudScribeLog.LongAudioExchange(logger, request.AttemptId, correlation, endpoint, post, sendStarted, callerCancelled, request.PayloadSha256,
            post ? request.PayloadBytes : 0, status, readState, bytes, duration, record, failure);
        if (headers is null) return; // No headers observed, not a fabricated absent header.
        foreach (var (name, kind) in new[] { ("x-goog-request-id", GoogleProviderRequestIdHeader.GoogleRequestId),
            ("x-request-id", GoogleProviderRequestIdHeader.RequestId), ("x-cloud-trace-context", GoogleProviderRequestIdHeader.CloudTraceContext) })
        {
            if (!headers.NonValidated.TryGetValues(name, out var values)) continue;
            string[] bounded = values.Take(2).ToArray();
            var state = bounded.Length != 1 ? GoogleProviderRequestIdState.Multiple : bounded[0].Length > 256 ? GoogleProviderRequestIdState.Oversized
                : string.IsNullOrWhiteSpace(bounded[0]) ? GoogleProviderRequestIdState.Invalid : GoogleProviderRequestIdState.PresentHashed;
            string? hash = state == GoogleProviderRequestIdState.PresentHashed ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bounded[0]))) : null;
            CloudScribeLog.GoogleRequestIdEvidence(logger, correlation, kind, state, hash); return;
        }
        CloudScribeLog.GoogleRequestIdEvidence(logger, correlation, GoogleProviderRequestIdHeader.None, GoogleProviderRequestIdState.Absent, null);
    }
}
