using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleGenerationHttpTransport
{
    private void RequireCompletionSynthesis(GoogleHttpTransportRequest request, byte[] payload, Guid correlation)
    {
        bool required = _completionGate?.BeforeSynthesis(request.CredentialReferenceId, request.Endpoint, payload,
            correlation, request.IdempotencyKeySha256) == true;
        if (required && _privateDiagnostics?.IsActiveFor(request.PrivateDiagnosticTicket, payload, requireProtectedEnvelope: true) != true)
            throw new PrivateDiagnosticNotReadyException();
    }
    private void ObserveMissingResponse(GoogleHttpTransportRequest request, string correlation, int status, byte[] input)
    {
        CloudScribeLog.Observe(() => _privateDiagnostics?.Observe(request.PrivateDiagnosticTicket,
            status, ReadOnlyMemory<byte>.Empty, input, string.Empty, correlation, false,
            status == 0 ? "NoResponseHeaders" : "HeadersWithoutBody", 0));
    }
    private void ObserveProviderResponse(Guid? ticket, string correlation, int status, byte[] bytes, byte[] input, string token)
    {
        if (status != 200) ObserveErrorResponse(correlation, status, bytes);
        var retention = _privateDiagnostics?.Observe(ticket, status, bytes, input, token, correlation)
            ?? GoogleExplanationRetentionState.NoActivePrivateTicket;
        if (status != 200) ObserveExplanationEvidence(correlation, status, bytes, retention);
    }

    private void EndTransportObservation(Guid? ticket, string correlation, GoogleSynthesisStage stage, int status, long started)
    {
        _privateDiagnostics?.End(ticket);
        Observe(() =>
        {
            long elapsed = checked((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            CloudScribeLog.GoogleTransportEnded(_logger, correlation, stage, status, elapsed);
        });
    }

    private void ObserveRequestId(string correlation, HttpResponseHeaders headers) => Observe(() =>
    {
        foreach (var (name, kind) in new[] { ("x-goog-request-id", GoogleProviderRequestIdHeader.GoogleRequestId),
            ("x-request-id", GoogleProviderRequestIdHeader.RequestId), ("x-cloud-trace-context", GoogleProviderRequestIdHeader.CloudTraceContext) })
        {
            if (!headers.NonValidated.TryGetValues(name, out var values)) continue;
            string[] bounded = values.Take(2).ToArray();
            var state = bounded.Length != 1 ? GoogleProviderRequestIdState.Multiple
                : bounded[0].Length > 256 ? GoogleProviderRequestIdState.Oversized
                : string.IsNullOrWhiteSpace(bounded[0]) ? GoogleProviderRequestIdState.Invalid : GoogleProviderRequestIdState.PresentHashed;
            string? hash = state == GoogleProviderRequestIdState.PresentHashed
                ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bounded[0]))) : null;
            CloudScribeLog.GoogleRequestIdEvidence(_logger, correlation, kind, state, hash);
            return;
        }
        CloudScribeLog.GoogleRequestIdEvidence(_logger, correlation, GoogleProviderRequestIdHeader.None, GoogleProviderRequestIdState.Absent, null);
    });

    private void ObserveExplanationEvidence(string correlation, int status, byte[] bytes, GoogleExplanationRetentionState retention) => Observe(() =>
        CloudScribeLog.GoogleExplanationEvidence(_logger, correlation, status, bytes.Length, retention));

    private void ObserveErrorResponse(string correlation, int status, byte[] bytes) => Observe(() =>
    {
        var details = GoogleErrorDetailParser.Parse(bytes);
        CloudScribeLog.GoogleSynthesisErrorDetails(_logger, correlation, status, details.Reason, details.Domain, details.Field);
        ObserveErrorParse(correlation, status, bytes);
    });

    private void ObserveWireContent(string correlation, string payloadHash, int bytes, Uri endpoint) => Observe(() =>
    {
        var identity = EndpointIdentity(endpoint);
        string origin = OriginHash(endpoint);
        bool hasQuery = !string.IsNullOrEmpty(endpoint.Query);
        CloudScribeLog.GoogleSynthesisWireContent(_logger, correlation, payloadHash, bytes, identity, origin, hasQuery);
    });

    private void ObserveErrorParse(string correlation, int status, byte[] bytes) => Observe(() =>
    {
        var diagnostic = GoogleErrorResponseDiagnosticParser.Parse(bytes);
        CloudScribeLog.GoogleSynthesisErrorParse(_logger, correlation, status, diagnostic.Parse, diagnostic.Status,
            diagnostic.Message, diagnostic.MessageUtf16Characters, diagnostic.Details, diagnostic.DetailCount, diagnostic.MessageHint);
    });

    private async Task<byte[]> ReadObservedResponseAsync(HttpResponseMessage response, int maximum, string correlation,
        Guid? privateTicket, byte[] input, string credential, CancellationToken token)
    {
        var observation = new ResponseReadObservation();
        var headers = CaptureHeaders(response.Content.Headers);
        try { return await ReadResponseBytesAsync(response, maximum, observation, token).ConfigureAwait(false); }
        finally
        {
            if (observation.State != GoogleResponseReadState.Complete)
                CloudScribeLog.Observe(() => _privateDiagnostics?.Observe(privateTicket, (int)response.StatusCode,
                    observation.Prefix.ToArray(), input, credential, correlation, false, observation.State.ToString(), observation.Bytes));
            Observe(() =>
            {
                var length = LengthState(headers, observation);
                CloudScribeLog.GoogleSynthesisResponseMetadata(_logger, correlation, (int)response.StatusCode, observation.Bytes,
                    observation.State, headers.MediaType, length);
            });
        }
    }

    private void Observe(Action action)
    {
        if (CloudScribeLog.ObserveIsEnabled(_logger, LogLevel.Information)) CloudScribeLog.Observe(action);
    }

    private static string OriginHash(Uri endpoint) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(endpoint.GetLeftPart(UriPartial.Authority))));

    private static GoogleSynthesisEndpointIdentity EndpointIdentity(Uri endpoint)
    {
        if (!string.Equals(endpoint.AbsolutePath, "/v1/text:synthesize", StringComparison.Ordinal) || !string.IsNullOrEmpty(endpoint.Query))
            return GoogleSynthesisEndpointIdentity.OtherPinnedEndpoint;
        if (string.Equals(endpoint.Host, "texttospeech.googleapis.com", StringComparison.OrdinalIgnoreCase))
            return GoogleSynthesisEndpointIdentity.GlobalV1Synthesize;
        return endpoint.Host.EndsWith("-texttospeech.googleapis.com", StringComparison.OrdinalIgnoreCase)
            ? GoogleSynthesisEndpointIdentity.RegionalV1Synthesize : GoogleSynthesisEndpointIdentity.OtherPinnedEndpoint;
    }

    private static GoogleResponseMediaType MediaType(HttpContentHeaders headers)
    {
        try
        {
            string? value = headers.ContentType?.MediaType;
            return value is null ? headers.Contains("Content-Type") ? GoogleResponseMediaType.Invalid : GoogleResponseMediaType.Absent
                : string.Equals(value, "application/json", StringComparison.OrdinalIgnoreCase) ? GoogleResponseMediaType.Json : GoogleResponseMediaType.Other;
        }
        catch (FormatException) { return GoogleResponseMediaType.Invalid; }
    }

    private static ResponseHeaderObservation CaptureHeaders(HttpContentHeaders headers)
    {
        try
        {
            // The parsed getter may replace even a malformed declared value with a computed length.
            if (!headers.NonValidated.TryGetValues("Content-Length", out var values)) return new(MediaType(headers), null, false);
            string[] bounded = values.Take(2).ToArray();
            if (bounded.Length != 1 || bounded[0].Length > 32) return new(MediaType(headers), null, true);
            string value = bounded[0].Trim();
            bool valid = value.Length > 0 && value.All(char.IsAsciiDigit)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            return new(MediaType(headers), valid ? long.Parse(value, CultureInfo.InvariantCulture) : null, !valid);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { return new(GoogleResponseMediaType.Invalid, null, true); }
    }

    private static GoogleResponseLengthState LengthState(ResponseHeaderObservation headers, ResponseReadObservation observation) =>
        headers.InvalidLength ? GoogleResponseLengthState.Invalid : headers.DeclaredLength is null ? GoogleResponseLengthState.Absent
        : observation.State != GoogleResponseReadState.Complete ? GoogleResponseLengthState.DeclaredButReadIncomplete
        : headers.DeclaredLength == observation.Bytes ? GoogleResponseLengthState.Matches : GoogleResponseLengthState.Mismatch;

    private static async Task<byte[]> ReadResponseBytesAsync(HttpResponseMessage response, int maximumResponseBytes,
        ResponseReadObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var rented = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                observation.Bytes = checked(observation.Bytes + read);
                if ((int)response.StatusCode != 200 && observation.Prefix.Count < 16384)
                    observation.Prefix.AddRange(rented.AsSpan(0, Math.Min(read, 16384 - observation.Prefix.Count)).ToArray());
                if (buffer.Length + read > maximumResponseBytes)
                {
                    observation.State = GoogleResponseReadState.Oversize;
                    throw new InvalidDataException("Google response exceeded the configured bounded response size.");
                }
                buffer.Write(rented, 0, read);
            }
            observation.State = GoogleResponseReadState.Complete;
            return buffer.ToArray();
        }
        catch (OperationCanceledException) { observation.State = GoogleResponseReadState.Cancelled; throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (observation.State != GoogleResponseReadState.Oversize) observation.State = GoogleResponseReadState.Failed;
            throw;
        }
    }

    private sealed class ResponseReadObservation
    {
        public int Bytes { get; set; }
        public GoogleResponseReadState State { get; set; } = GoogleResponseReadState.Reading;
        public List<byte> Prefix { get; } = [];
    }

    private sealed record ResponseHeaderObservation(GoogleResponseMediaType MediaType, long? DeclaredLength, bool InvalidLength);
}
