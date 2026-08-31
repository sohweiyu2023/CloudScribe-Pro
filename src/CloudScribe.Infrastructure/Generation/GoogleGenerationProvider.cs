using System.Net;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class GoogleGenerationProvider : IGenerationProvider
{
    public const string StableProviderId = "google-cloud-text-to-speech";
    public const string SynthesizeOperationStableId = "synthesize-speech";

    private readonly GoogleGenerationAccount _account;
    private readonly GoogleGenerationHttpTransport _transport;
    private readonly int _maximumAudioBytes;

    public GoogleGenerationProvider(
        GoogleGenerationAccount account,
        GoogleGenerationHttpTransport transport,
        int maximumAudioBytes = 64 * 1024 * 1024)
    {
        _account = (account ?? throw new ArgumentNullException(nameof(account))).Validate();
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (maximumAudioBytes is <= 0 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumAudioBytes));
        _maximumAudioBytes = maximumAudioBytes;
    }

    public string ProviderStableId => StableProviderId;

    public async Task<GenerationProviderResponse> SubmitAsync(
        GenerationProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestIdentity(request);

        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null)
            return Unknown("google-transport-unknown");
        if (response.Value.TimedOut)
            return Unknown("google-transport-timeout");
        if (response.Value.Response is null)
            return Unknown("google-transport-unknown");

        return response.Value.Response.StatusCode == HttpStatusCode.OK
            ? CreateAcceptedResponse(response.Value.Response, request.OutputFormat)
            : CreateRejectedResponse(response.Value.Response);
    }

    public Task<GenerationProviderResponse?> ReconcileAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();

        // The synchronous Google TTS request used by this adapter does not expose a safe
        // provider-side lookup by client idempotency key. Returning null is intentional:
        // callers must keep ambiguous submissions reconciliation-gated and must never
        // reinterpret "not found" as permission to duplicate a billable request.
        return Task.FromResult<GenerationProviderResponse?>(null);
    }

    private void ValidateRequestIdentity(GenerationProviderRequest request)
    {
        if (!string.Equals(request.ProviderStableId, StableProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generation request provider identity does not match the Google adapter.");
        if (!string.Equals(request.OperationStableId, SynthesizeOperationStableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generation request operation identity does not match Google synthesize-speech.");
        if (!string.Equals(request.AccountId, _account.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generation request account identity does not match the pinned Google account.");
    }

    private async Task<(GoogleHttpTransportResponse? Response, bool TimedOut)?> SendAsync(
        GenerationProviderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.SendAsync(
                new GoogleHttpTransportRequest(
                    _account.Endpoint,
                    _account.CredentialReferenceId,
                    request.CompiledPayload),
                cancellationToken).ConfigureAwait(false);
            return (response, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private GenerationProviderResponse CreateAcceptedResponse(GoogleHttpTransportResponse response, string outputFormat)
    {
        GoogleGenerationParsedResponse parsed;
        try
        {
            parsed = GoogleGenerationResponseParser.Parse(response.Body, _maximumAudioBytes);
        }
        catch (InvalidDataException)
        {
            return new GenerationProviderResponse(
                SubmissionDisposition.Accepted,
                "google-response-unusable",
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                "google-accepted-invalid-media");
        }

        return new GenerationProviderResponse(
            SubmissionDisposition.Accepted,
            parsed.ProviderOperationId ?? "google-sync-response",
            parsed.AudioBytes,
            ContentTypeFor(outputFormat),
            null,
            "google-accepted");
    }

    private static GenerationProviderResponse CreateRejectedResponse(GoogleHttpTransportResponse response)
    {
        var classification = GoogleProviderResponsePolicy.Classify((int)response.StatusCode, response.RetryAfter, false);
        var disposition = classification.Disposition is GoogleRetryDisposition.RetryAfter or GoogleRetryDisposition.Backoff
            ? SubmissionDisposition.RejectedSafeToRetry
            : SubmissionDisposition.NotSubmitted;

        return new GenerationProviderResponse(
            disposition,
            null,
            ReadOnlyMemory<byte>.Empty,
            null,
            response.RetryAfter,
            $"google-http-{(int)response.StatusCode}");
    }

    private static GenerationProviderResponse Unknown(string diagnosticCode) => new(
        SubmissionDisposition.UnknownRequiresReconciliation,
        null,
        ReadOnlyMemory<byte>.Empty,
        null,
        null,
        diagnosticCode);

    private static string ContentTypeFor(string outputFormat) => outputFormat.Trim().ToLowerInvariant() switch
    {
        "wav" => "audio/wav",
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "ogg" => "audio/ogg",
        _ => "application/octet-stream",
    };
}
