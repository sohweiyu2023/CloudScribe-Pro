using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudScribe.Infrastructure.Configuration;
using CloudScribe.Domain.Generation;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Generation;

public sealed class CompletionGateCommandRunner : ICompletionGateCommandRunner
{
    public void Run(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
        => _ = Execute(configuration, command, id, identity, receipt);

    public CompletionSynthesisGateDecision CheckSynthesis(CompletionGateConfiguration configuration, Guid correlation, string payloadSha256)
    {
        var result = Execute(configuration, "check-diagnostic-dispatch", correlation, payloadSha256, null);
        bool required = result.GetProperty("requireProtectedCapture").GetBoolean();
        var member = result.GetProperty("idempotencyKeySha256");
        string? key = member.ValueKind == JsonValueKind.Null ? null : member.GetString();
        if (required ? key is null || key.Length != 64 || !key.All(char.IsAsciiHexDigit) : key is not null)
            throw new CompletionGateException(CompletionGateFailure.Protocol);
        return new(required, key);
    }

    private static JsonElement Execute(CompletionGateConfiguration configuration, string command, Guid id, string identity, IReadOnlyDictionary<string, object?>? receipt)
    {
        if (!string.Equals(CompletionWorkflowGate.Hash(CompletionWorkflowGate.Read(configuration.PythonPath, 32 * 1024 * 1024)), configuration.PythonSha256, StringComparison.Ordinal)
            || !string.Equals(CompletionWorkflowGate.Hash(CompletionWorkflowGate.Read(configuration.ScriptPath, 131072)), configuration.ScriptSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Completion gate executable/source pin changed.");
        var start = new ProcessStartInfo(configuration.PythonPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-S"); start.ArgumentList.Add(configuration.ScriptPath);
        using var process = Process.Start(start) ?? throw new IOException("Completion gate process did not start.");
        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["configPath"] = configuration.ConfigPath,
            ["configSha256"] = configuration.Pin,
            ["appDirectory"] = configuration.ApplicationDirectory,
            ["id"] = id.ToString("D"),
            ["identitySha256"] = identity,
            ["receipt"] = receipt
        };
        if (string.Equals(command, "check-diagnostic-dispatch", StringComparison.Ordinal)) request["payloadSha256"] = identity;
        string input = JsonSerializer.Serialize(request);
        process.StandardInput.Write(input); process.StandardInput.Close();
        // Bound both streams without logging subprocess prose. No process kill or
        // retry on timeout: a possibly committed operation requires reconciliation.
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        if (!process.WaitForExit(15000)) throw new IOException("Completion gate outcome is uncertain after timeout.");
        string result = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(result);
        ChirpPolicyFiles.RequireUnique(json.RootElement);
        RequireResponse(json.RootElement, command, id, identity, configuration, process.ExitCode);
        return json.RootElement.Clone();
    }

    private static void RequireResponse(JsonElement result, string command, Guid id, string identity, CompletionGateConfiguration configuration, int exit)
    {
        if (exit != 0 || result.TryGetProperty("rejected", out _))
        {
            var reason = result.TryGetProperty("reason", out var value) && Enum.TryParse<CompletionGateFailure>(value.GetString(), out var parsed)
                && Enum.IsDefined(parsed) ? parsed : CompletionGateFailure.Unknown;
            var code = result.TryGetProperty("code", out var member) && member.ValueKind == JsonValueKind.String
                && Enum.TryParse<CompletionProtocolCode>(member.GetString(), out var detailCode) && Enum.IsDefined(detailCode)
                ? detailCode : CompletionProtocolCode.Unknown;
            var failureType = result.TryGetProperty("failureExceptionType", out var typeMember) && typeMember.ValueKind == JsonValueKind.String
                && Enum.TryParse<CompletionProtocolExceptionType>(typeMember.GetString(), out var kind) && Enum.IsDefined(kind) ? kind : CompletionProtocolExceptionType.Other;
            var component = result.TryGetProperty("failureComponent", out var componentMember) && componentMember.ValueKind == JsonValueKind.String
                && Enum.TryParse<CompletionProtocolFailureComponent>(componentMember.GetString(), true, out var part) && Enum.IsDefined(part)
                ? part : CompletionProtocolFailureComponent.Unavailable;
            int line = result.TryGetProperty("failureSourceLine", out var lineMember) && lineMember.ValueKind == JsonValueKind.Number
                && lineMember.TryGetInt32(out int number) && number is >= 1 and <= 10000 ? number : 0;
            throw new CompletionGateException(reason, code, failureType, component, line);
        }
        if (command.StartsWith("claim-", StringComparison.Ordinal)
            && (result.GetProperty("id").GetGuid() != id
                || !string.Equals(result.GetProperty("identitySha256").GetString(), identity, StringComparison.Ordinal)
                || !string.Equals(result.GetProperty("scopeSha256").GetString(), configuration.ScopeSha256, StringComparison.Ordinal)))
            throw new CompletionGateException(CompletionGateFailure.Protocol);
        if ((string.Equals(command, "status", StringComparison.Ordinal) && result.GetProperty("requiresReconciliation").GetBoolean())
            || (string.Equals(command, "check", StringComparison.Ordinal) && !result.GetProperty("valid").GetBoolean()))
            throw new CompletionGateException(CompletionGateFailure.PendingOutcome);
        if (string.Equals(command, "check-diagnostic-dispatch", StringComparison.Ordinal)
            && (!result.GetProperty("valid").GetBoolean()
                || result.GetProperty("id").GetGuid() != id
                || !string.Equals(result.GetProperty("payloadSha256").GetString(), identity, StringComparison.Ordinal)))
            throw new CompletionGateException(CompletionGateFailure.PinMismatch);
        if (string.Equals(command, "check-diagnostic-admission", StringComparison.Ordinal)
            && (!result.GetProperty("valid").GetBoolean()
                || !string.Equals(result.GetProperty("policySha256").GetString(), identity, StringComparison.Ordinal)))
            throw new CompletionGateException(CompletionGateFailure.PinMismatch);
        if (string.Equals(command, "complete", StringComparison.Ordinal) && !string.Equals(result.GetProperty("outcome").GetString(), "Succeeded", StringComparison.Ordinal))
            throw new CompletionGateException(result.TryGetProperty("reason", out var detail) && Enum.TryParse<CompletionGateFailure>(detail.GetString(), out var completionReason)
                && Enum.IsDefined(completionReason) ? completionReason : CompletionGateFailure.Stopped);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[8193]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(count)).ConfigureAwait(false);
            if (read == 0) return new string(buffer, 0, count);
            count += read;
        }
        throw new InvalidDataException("Completion gate output exceeded its bound.");
    }
}
