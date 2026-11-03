using System.Net;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class PrivateProviderDiagnosticsTests
{
    private static readonly int[] SyntheticAudioLookingArray = [82, 73, 70, 70];
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProtectedEnvelopeRetainsUnknownExplanationPrivatelyAcrossStatusesAndRestart(int status)
    {
        using var f = new Fixture(); ArmEnvelope(f);
        f.Handler.Status = (HttpStatusCode)status;
        f.Handler.Body = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = status,
                message = "Novel boundary evidence",
                unknownConstraint = new { explanation = "Previously unrecognized explanation", client_secret = "SYNTHETIC_CLIENT_SECRET" },
                audioContent = new string('A', 200),
                token = "SYNTHETIC_SECRET_ACCESS_TOKEN"
            }
        });
        var outcome = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        Assert.False(outcome.IsAccepted); Assert.Equal(1, f.Handler.Calls);
        var record = Assert.Single(new GooglePrivateErrorDiagnostics(f.Root, f.Clock).ReadRecords());
        var envelope = Assert.IsType<PrivateProviderResponseEnvelope>(record.Response);
        Assert.Equal(status, envelope.HttpStatus); Assert.True(envelope.ReadCompleted);
        Assert.Contains("Previously unrecognized explanation", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_CLIENT_SECRET", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("audioContent", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.True(envelope.DetailsOmitted); Assert.True(envelope.RedactionApplied);
        string path = Assert.Single(Directory.GetFiles(f.Root)); Assert.EndsWith(".pdiag", path, StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length <= 65536);
        Assert.DoesNotContain("Novel boundary evidence", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("Previously unrecognized", record.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Previously unrecognized", envelope.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>Upstream failure. Bearer SYNTHETIC_SECRET_ACCESS_TOKEN</html>", "NonJsonOrMalformed")]
    [InlineData("", "Empty")]
    [InlineData("{\"audioContent\": invalid", "UnstructuredContentExcluded")]
    public async Task NonJsonAndEmptyBodiesRetainExplicitEvidenceWithoutRawFallback(string body, string expected)
    {
        using var f = new Fixture(); ArmEnvelope(f); f.Handler.Body = body;
        _ = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var envelope = Assert.IsType<PrivateProviderResponseEnvelope>(Assert.Single(f.Store.ReadRecords()).Response);
        Assert.Equal(expected, envelope.ParseState);
        Assert.DoesNotContain("SYNTHETIC_SECRET_ACCESS_TOKEN", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", envelope.RedactedBody, StringComparison.Ordinal);
        Assert.False(f.Store.IsArmed);
    }

    [Fact]
    public async Task PartialOversizeReadRetainsPrefixButPreservesOriginalTransportFailure()
    {
        using var f = new Fixture(); ArmEnvelope(f); f.Handler.Body = "Provider failure prefix " + new string('x', 120);
        using var http = new HttpClient(f.Handler);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: f.Store);
        var begin = typeof(GooglePrivateErrorDiagnostics).GetMethod("Begin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var ticket = (Guid?)begin.Invoke(f.Store, [f.Request, f.Binding.CredentialReferenceId, false]);
        var request = new GoogleHttpTransportRequest(new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"),
            f.Binding.CredentialReferenceId, f.Request.CompiledPayload, 32, ticket, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync(request, TestContext.Current.CancellationToken));
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var envelope = Assert.IsType<PrivateProviderResponseEnvelope>(Assert.Single(f.Store.ReadRecords()).Response);
        Assert.False(envelope.ReadCompleted); Assert.Equal("Oversize", envelope.ReadState);
        Assert.True(envelope.DetailsOmitted); Assert.True(envelope.Truncated); Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task ProtectedRecordTamperCannotFallbackToPlaintextAndExpiryStillApplies()
    {
        using var f = new Fixture(); ArmEnvelope(f); await f.SendAsync("Private failure evidence");
        string path = Assert.Single(Directory.GetFiles(f.Root)); byte[] original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, "{\"Message\":\"plaintext substitution\"}"u8.ToArray());
        Assert.Empty(f.Store.ReadRecords());
        File.WriteAllBytes(path, original); Assert.Single(f.Store.ReadRecords());
        f.Clock.Now += TimeSpan.FromHours(24); Assert.Empty(f.Store.ReadRecords()); Assert.Empty(Directory.GetFiles(f.Root));
    }

    [Fact]
    public async Task SuccessNeverCapturesAudioAndTicketCannotObserveAnotherResponse()
    {
        using var f = new Fixture(); ArmEnvelope(f); f.Handler.Status = HttpStatusCode.OK;
        f.Handler.Body = "{\"audioContent\":\"" + Convert.ToBase64String("SYNTHETIC AUDIO"u8) + "\"}";
        _ = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var envelope = Assert.IsType<PrivateProviderResponseEnvelope>(Assert.Single(f.Store.ReadRecords()).Response);
        Assert.Equal("SuccessBodyExcluded", envelope.ParseState); Assert.Empty(envelope.RedactedBody);
        f.Handler.Status = HttpStatusCode.BadRequest; await f.SendAsync("Later response");
        Assert.Single(f.Store.ReadRecords()); Assert.False(f.Store.IsArmed);
    }

    [Theory]
    [InlineData("AQIDBA==")]
    [InlineData("AQID\nBAUG")]
    [InlineData("AQID-BA_")]
    [InlineData("data:audio/wav;base64,AQIDBA==")]
    [InlineData("Provider returned data:application/octet-stream;base64,AQIDBA== unexpectedly.")]
    [InlineData("Provider returned AQIDBA== unexpectedly.")]
    public async Task AmbiguousShortWrappedAndUrlSafeEncodedFieldsAreOmitted(string encoded)
    {
        using var f = new Fixture(); ArmEnvelope(f);
        f.Handler.Body = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "Provider rejected this request.",
                unknown = encoded,
                samples = SyntheticAudioLookingArray
            }
        });
        _ = await f.Provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
        var response = Assert.IsType<PrivateProviderResponseEnvelope>(Assert.Single(f.Store.ReadRecords()).Response);
        Assert.DoesNotContain(encoded, response.RedactedBody, StringComparison.Ordinal); Assert.True(response.DetailsOmitted);
        Assert.DoesNotContain("82,73", response.RedactedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptExistingStoreFailsBeforeTicketCanBeArmed()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        File.WriteAllText(Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".pdiag"), "not protected data");
        Assert.Throws<InvalidOperationException>(() => ArmEnvelope(f));
        Assert.False(f.Store.IsArmed); Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task FailedPublishDoesNotDeleteAnyPreviouslyRetainedPrivateEvidence()
    {
        using var f = new Fixture();
        for (int i = 0; i < 8; i++) { f.Clock.Now += TimeSpan.FromSeconds(1); f.Arm(); await f.SendAsync("Previously retained explanation " + i); }
        var records = f.Store.ReadRecords(); Assert.Equal(8, records.Count);
        var newest = records.MaxBy(x => x.CreatedUtc)!;
        var storeType = typeof(GooglePrivateErrorDiagnostics).Assembly.GetType("CloudScribe.Infrastructure.Generation.FilePrivateProviderErrorStore")!;
        var persistence = (IPrivateProviderErrorPersistence)Activator.CreateInstance(storeType, [f.Root, f.Clock])!;
        Assert.Throws<IOException>(() => persistence.Save(newest with { Message = "Synthetic collision must not replace this record" }));
        Assert.Equal(records.OrderBy(x => x.Id), f.Store.ReadRecords().OrderBy(x => x.Id));
        Assert.Equal(8, Directory.GetFiles(f.Root).Length);
    }

    private static void ArmEnvelope(Fixture f)
    {
        var binding = f.Binding with { CaptureMode = PrivateErrorCaptureMode.ProtectedResponseEnvelope };
        f.Store.Arm(binding, true); f.Store.Attach(f.Request, binding);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("selected-only")]
    [InlineData("wrong-owner")]
    [InlineData("protected")]
    public async Task DiagnosticScopeCannotBeBypassedWithGenericRequestKeyAndSamePayload(string mode)
    {
        using var f = new Fixture(); // Generic key deliberately has no chirp-article prefix.
        if (string.Equals(mode, "selected-only", StringComparison.Ordinal)) f.Arm();
        else if (!string.Equals(mode, "off", StringComparison.Ordinal)) ArmEnvelope(f);
        string key = string.Equals(mode, "wrong-owner", StringComparison.Ordinal) ? "another-owned-key" : f.Request.IdempotencyKey;
        var runner = new ProtectedScopeRunner(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        var gate = new CompletionWorkflowGate(CompletionGatePayloadTests.Configuration, runner);
        using var http = new HttpClient(f.Handler, disposeHandler: false);
        var transport = new GoogleGenerationHttpTransport(http, new Credential(), new Uri("https://texttospeech.googleapis.com"), privateDiagnostics: f.Store, completionGate: gate);
        var provider = new GoogleGenerationProvider(new("synthetic-account", "synthetic-reference", new Uri("https://texttospeech.googleapis.com/v1/text:synthesize"), "global"), transport);
        if (string.Equals(mode, "protected", StringComparison.Ordinal))
        {
            _ = await provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken);
            Assert.Equal(1, f.Handler.Calls); Assert.Equal(2, runner.Correlations.Count);
            Assert.Equal(runner.Correlations[0], runner.Correlations[1]);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => provider.SubmitAsync(f.Request, TestContext.Current.CancellationToken));
            Assert.True(error is CompletionGateException or PrivateDiagnosticNotReadyException);
            Assert.Equal(0, f.Handler.Calls);
        }
        await f.Store.WaitForPendingWriteAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ProtectedScopeRunner(string keySha256) : ICompletionGateCommandRunner
    {
        public List<Guid> Correlations { get; } = [];
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt) { }
        public CompletionSynthesisGateDecision CheckSynthesis(CompletionGateConfiguration configuration, Guid correlation, string payloadSha256)
        { Correlations.Add(correlation); return new(true, keySha256); }
    }
}
