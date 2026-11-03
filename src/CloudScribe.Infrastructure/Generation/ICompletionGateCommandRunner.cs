using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public interface ICompletionGateCommandRunner
{
    void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt);
    CompletionSynthesisGateDecision CheckSynthesis(CompletionGateConfiguration configuration, Guid correlation, string payloadSha256)
        => throw new CompletionGateException(CompletionGateFailure.Protocol);
}
