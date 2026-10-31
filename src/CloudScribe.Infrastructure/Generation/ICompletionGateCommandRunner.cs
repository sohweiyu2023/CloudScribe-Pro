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
}
