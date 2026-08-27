using System.Text;
using CloudScribe.Application.Generation;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Application.Tests;

public sealed class Stage7ProviderBackedMultiSpeakerTurnExecutorTests
{
    [Fact]
    public async Task Unsupported_output_format_is_rejected_before_provider_submission()
    {
        var provider = new RecordingProvider(Accepted(CreateWaveBytes(), "audio/wav"));
        var executor = CreateExecutor(provider, "ogg");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            executor.ExecuteAsync(CreateTurn(), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(0, provider.Submissions);
    }

    [Fact]
    public async Task Accepted_corrupt_media_fails_closed()
    {
        var provider = new RecordingProvider(Accepted(new byte[] { 1, 2, 3 }, "audio/wav"));
        var executor = CreateExecutor(provider, "wav");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(CreateTurn(), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(1, provider.Submissions);
    }

    [Fact]
    public async Task Accepted_media_must_match_requested_format()
    {
        var provider = new RecordingProvider(Accepted(CreateWaveBytes(), "audio/wav"));
        var executor = CreateExecutor(provider, "mp3");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(CreateTurn(), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(1, provider.Submissions);
    }

    [Fact]
    public async Task Structurally_valid_requested_media_completes_turn()
    {
        var provider = new RecordingProvider(Accepted(CreateWaveBytes(), "audio/wav"));
        var executor = CreateExecutor(provider, "wav");

        var outcome = await executor.ExecuteAsync(
            CreateTurn(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.RequiresReconciliation);
        Assert.Equal("accepted", outcome.DiagnosticCode);
        Assert.Equal(1, provider.Submissions);
    }

    private static ProviderBackedMultiSpeakerTurnExecutor CreateExecutor(
        IGenerationProvider provider,
        string outputFormat) =>
        new(
            _ => provider,
            _ => new GenerationProviderRequest(
                "provider",
                "synthesize",
                "account",
                "turn-0",
                new byte[] { 1 },
                outputFormat));

    private static PlannedSpeakerTurn CreateTurn() =>
        new(0, "speaker", "hello", new SpeakerRoute("speaker", "provider", "voice", false));

    private static GenerationProviderResponse Accepted(byte[] media, string contentType) =>
        new(
            SubmissionDisposition.Accepted,
            "request-1",
            media,
            contentType,
            null,
            "accepted");

    private static byte[] CreateWaveBytes()
    {
        var bytes = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BitConverter.GetBytes(36U).CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BitConverter.GetBytes(16U).CopyTo(bytes, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16_000U).CopyTo(bytes, 24);
        BitConverter.GetBytes(32_000U).CopyTo(bytes, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(bytes, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BitConverter.GetBytes(0U).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class RecordingProvider : IGenerationProvider
    {
        private readonly GenerationProviderResponse _response;

        public RecordingProvider(GenerationProviderResponse response) => _response = response;

        public string ProviderStableId => "provider";

        public int Submissions { get; private set; }

        public Task<GenerationProviderResponse> SubmitAsync(
            GenerationProviderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Submissions++;
            return Task.FromResult(_response);
        }

        public Task<GenerationProviderResponse?> ReconcileAsync(
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<GenerationProviderResponse?>(null);
        }
    }
}
