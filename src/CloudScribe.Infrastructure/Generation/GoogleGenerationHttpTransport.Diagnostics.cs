using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Logging;
using CloudScribe.Domain.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Generation;

public sealed partial class GoogleGenerationHttpTransport
{
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

    private async Task<byte[]> ReadObservedResponseAsync(HttpResponseMessage response, int maximum, string correlation, CancellationToken token)
    {
        var observation = new ResponseReadObservation();
        var headers = CaptureHeaders(response.Content.Headers);
        try { return await ReadResponseBytesAsync(response, maximum, observation, token).ConfigureAwait(false); }
        finally
        {
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
    }

    private sealed record ResponseHeaderObservation(GoogleResponseMediaType MediaType, long? DeclaredLength, bool InvalidLength);
}
