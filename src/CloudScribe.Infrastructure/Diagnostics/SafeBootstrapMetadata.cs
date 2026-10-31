using System.Globalization;
using CloudScribe.Application.Startup;

namespace CloudScribe.Infrastructure.Diagnostics;

/// <summary>Adapter for bounded product bootstrap metadata; never retains arbitrary message text.</summary>
public static class SafeBootstrapMetadata
{
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "process.start", "process.exit", "process.unhandled-exception", "task.unobserved-exception", "process.fatal-startup",
        "startup.phase-summary", "native.action", "native.metadata-unavailable", "application.initialized", "window.opened",
        "window.first-render", "window.settled", "window.activated", "window.state-changed", "window.closed",
        "structured-logging.ready", "structured-logging.unavailable",
    };
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal) { "Import", "Export", "Play", "Unknown" };
    private static readonly HashSet<string> NativePhases = new(StringComparer.Ordinal) { "Open", "Cancel", "Complete", "ShellOpened", "Unavailable", "Unsupported", "Failed", "Unknown" };
    private static readonly HashSet<string> WindowStates = new(StringComparer.Ordinal) { "Normal", "Minimized", "Maximized", "FullScreen", "none" };

    public static SafeBootstrapMetadataSnapshot Capture(string eventName, string message)
    {
        if (!Events.Contains(eventName) || message.Length > 16_384)
            return new("unregistered.bootstrap", new Dictionary<string, string>(StringComparer.Ordinal), false, []);
        if (string.Equals(eventName, "startup.phase-summary", StringComparison.Ordinal))
            return CapturePhases(message);
        return ParseFields(eventName, message);
    }

    private static SafeBootstrapMetadataSnapshot ParseFields(string eventName, string message)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        bool complete = true;
        int count = 0;
        foreach (string item in message.Split(';', StringSplitOptions.TrimEntries))
        {
            if (++count > 64) { complete = false; break; }
            int equal = item.IndexOf('=');
            if (equal < 1) { complete = false; continue; }
            string key = item[..equal]; string value = item[(equal + 1)..];
            if (!TryField(eventName, key, value, out string? safe) || !fields.TryAdd(key, safe!)) complete = false;
        }
        return new(eventName, fields, complete, []);
    }

    public static SafeBootstrapMetadataSnapshot Export(string eventName, IReadOnlyDictionary<string, string?> supplied)
    {
        if (!Events.Contains(eventName)) return new("unregistered.bootstrap", new Dictionary<string, string>(StringComparer.Ordinal), false, []);
        var result = new Dictionary<string, string>(StringComparer.Ordinal); bool complete = true;
        foreach (var item in supplied.Take(65))
            if (item.Value is null || !TryField(eventName, item.Key, item.Value, out var safe) || !result.TryAdd(item.Key, safe!)) complete = false;
        return new(eventName, result, complete, []);
    }

    private static SafeBootstrapMetadataSnapshot CapturePhases(string message)
    {
        string[] entries = message.Split(" | ", StringSplitOptions.None);
        var phases = new List<SafeStartupPhaseObservation>(64);
        bool complete = entries.Length is > 0 and <= 64;
        foreach (string entry in entries.Take(64))
        {
            var parsed = ParseFields("startup.phase-summary", entry);
            var phase = ReadPhase(parsed.Fields);
            if (phase is null || phases.Count != 0 && phase.Sequence <= phases[^1].Sequence) { complete = false; continue; }
            phases.Add(phase); complete &= parsed.FieldsComplete;
        }
        return new("startup.phase-summary", new Dictionary<string, string>(StringComparer.Ordinal), complete, phases.ToArray());
    }

    public static SafeStartupPhaseObservation? ReadPhase(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.Count != 6 || !fields.TryGetValue("sequence", out var sequence) || !fields.TryGetValue("phase", out var phase)
            || !fields.TryGetValue("state", out var state) || !fields.TryGetValue("phaseMs", out var phaseMs)
            || !fields.TryGetValue("startupMs", out var startupMs) || !fields.TryGetValue("budget", out var budget)
            || !int.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number is < 1 or > 64
            || !Enum.TryParse<StartupPhase>(phase, out var knownPhase) || !Enum.IsDefined(knownPhase)
            || !Enum.TryParse<StartupPhaseState>(state, out var knownState) || !Enum.IsDefined(knownState)
            || !Enum.TryParse<StartupBudgetState>(budget, out var knownBudget) || !Enum.IsDefined(knownBudget)
            || !long.TryParse(phaseMs, NumberStyles.None, CultureInfo.InvariantCulture, out long elapsed) || elapsed < 0
            || !long.TryParse(startupMs, NumberStyles.None, CultureInfo.InvariantCulture, out long total) || total < 0) return null;
        return new(number, knownPhase, knownState, elapsed, total, knownBudget);
    }

    private static bool TryField(string eventName, string key, string value, out string? safe)
    {
        safe = null;
        if (key.Length > 32 || value.Length > 128) return false;
        bool phaseSummary = string.Equals(eventName, "startup.phase-summary", StringComparison.Ordinal);
        if (key is "session" or "module" && Guid.TryParse(value, out var id)) safe = id.ToString("D");
        else if (key is "runtime" or "version") safe = SafeVersion(value);
        else if (key is "arguments" or "innerExceptionCount" or "styles" or "sequence" or "phaseMs" or "startupMs"
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long count) && count >= 0) safe = count.ToString(CultureInfo.InvariantCulture);
        else if (key is "hresult" or "exitCode" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) safe = code.ToString(CultureInfo.InvariantCulture);
        else if (string.Equals(key, "terminating", StringComparison.Ordinal) && bool.TryParse(value, out bool boolean)) safe = boolean ? "true" : "false";
        else if (string.Equals(key, "phase", StringComparison.Ordinal) && phaseSummary) safe = Defined<StartupPhase>(value);
        else if (string.Equals(key, "state", StringComparison.Ordinal) && phaseSummary) safe = Defined<StartupPhaseState>(value);
        else if (string.Equals(key, "budget", StringComparison.Ordinal) && phaseSummary) safe = Defined<StartupBudgetState>(value);
        else if (string.Equals(key, "operation", StringComparison.Ordinal) && Operations.Contains(value)) safe = value;
        else if (string.Equals(key, "phase", StringComparison.Ordinal) && NativePhases.Contains(value)) safe = value;
        else if (string.Equals(key, "state", StringComparison.Ordinal) && WindowStates.Contains(value)) safe = value;
        else if (string.Equals(key, "exceptionCategory", StringComparison.Ordinal)) safe = Defined<SafeExceptionCategory>(value);
        else if (string.Equals(key, "opacity", StringComparison.Ordinal) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity)
            && double.IsFinite(opacity) && opacity is >= 0 and <= 1) safe = opacity.ToString("R", CultureInfo.InvariantCulture);
        else if (string.Equals(key, "architecture", StringComparison.Ordinal) && value is "X64" or "X86" or "Arm64" or "Arm") safe = value;
        else if (string.Equals(key, "transparency", StringComparison.Ordinal) && value is "None" or "Transparent" or "Blur" or "AcrylicBlur" or "Mica") safe = value;
        else if (string.Equals(key, "mode", StringComparison.Ordinal) && value is "configured-application-data" or "executable-folder" or "local-app-data-fallback") safe = value;
        return safe is not null;
    }

    private static string? Defined<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed.ToString() : null;
    private static string? SafeVersion(string value)
    {
        string[] parts = value.Split('+');
        if (parts.Length > 2 || !Version.TryParse(parts[0], out var version)) return null;
        if (parts.Length == 1) return version.ToString();
        return parts[1].Length is >= 7 and <= 64 && parts[1].All(char.IsAsciiHexDigit) ? version + "+" + parts[1].ToLowerInvariant() : null;
    }
}
