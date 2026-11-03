using System.Security.Cryptography;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Infrastructure.Tests;

public sealed class CompletionGatePayloadTests
{
    private static readonly Uri Endpoint = new("https://texttospeech.googleapis.com/v1/text:synthesize");

    [Fact]
    public void SynthesisGateReceivesExactWireHashWithoutTextAndDoesNotInferPaidAuthority()
    {
        var runner = new Runner(); var gate = new CompletionWorkflowGate(Configuration, runner);
        byte[] bytes = "synthetic exact wire payload"u8.ToArray();
        gate.BeforeSynthesis("synthetic-reference", Endpoint, bytes, Guid.NewGuid());
        Assert.Collection(runner.Calls, x => Assert.Equal("status", x.Command), x => Assert.Equal("check-diagnostic-dispatch", x.Command));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), runner.Calls[1].Identity);
        Assert.All(runner.Calls, x => Assert.DoesNotContain("synthetic exact wire payload", x.Identity, StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredGateRejectsMissingWireIdentityButOrdinaryInactiveApplicationIsUnchanged()
    {
        var runner = new Runner(); var required = new CompletionWorkflowGate(Configuration, runner);
        Assert.Throws<CompletionGateException>(() => required.BeforeSynthesis("synthetic-reference", Endpoint));
        Assert.Single(runner.Calls); // Status check is not dispatch permission.
        var ordinary = new CompletionWorkflowGate(() => null, runner);
        ordinary.BeforeSynthesis("synthetic-reference", Endpoint);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("state")]
    public void ChangedAccountOrRejectedStateCannotReachDispatchCheck(string failure)
    {
        var runner = new Runner { RejectStatus = string.Equals(failure, "state", StringComparison.Ordinal) };
        var gate = new CompletionWorkflowGate(Configuration, runner);
        Assert.Throws<InvalidOperationException>(() => gate.BeforeSynthesis(
            string.Equals(failure, "identity", StringComparison.Ordinal) ? "wrong-reference" : "synthetic-reference", Endpoint, "{}"u8.ToArray(), Guid.NewGuid()));
        Assert.DoesNotContain(runner.Calls, x => string.Equals(x.Command, "check-diagnostic-dispatch", StringComparison.Ordinal));
    }

    internal static CompletionGateConfiguration Configuration() => new(1, "synthetic", "", "synthetic", "", "", "synthetic", new string('a', 64),
        "synthetic-app", "synthetic-app", new string('b', 64), new string('c', 64), new string('d', 64),
        "synthetic-account", "synthetic-project", "synthetic-reference", "https://texttospeech.googleapis.com/v1/voices",
        Endpoint.AbsoluteUri, "synthetic-manifest", new string('e', 64), "cmn-CN-Chirp3-HD-Alnilam", "cmn-CN");

    private sealed class Runner : ICompletionGateCommandRunner
    {
        public List<(string Command, string Identity)> Calls { get; } = [];
        public bool RejectStatus { get; init; }
        public CompletionSynthesisGateDecision CheckSynthesis(CompletionGateConfiguration configuration, Guid correlation, string payloadSha256)
        { Run(configuration, "check-diagnostic-dispatch", correlation, payloadSha256, null); return new(false, null); }
        public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
        {
            Calls.Add((command, identity));
            if (RejectStatus && string.Equals(command, "status", StringComparison.Ordinal)) throw new InvalidOperationException("Synthetic pending outcome.");
        }
    }
}
