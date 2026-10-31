using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudScribe.Application.Startup;
using CloudScribe.Infrastructure.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SafeBootstrapMetadataTests
{
    [Fact]
    public void FullBoundedPreloggerPhaseSequenceSurvivesWithoutFreeText()
    {
        string message = string.Join(" | ", Enumerable.Range(1, 64).Select(number => string.Create(CultureInfo.InvariantCulture,
            $"sequence={number}; phase=HostComposition; state=Started; phaseMs={number}; startupMs={number}; budget=NotArmed")));
        var result = SafeBootstrapMetadata.Capture("startup.phase-summary", message);
        Assert.True(result.FieldsComplete);
        Assert.Equal(64, result.StartupPhases.Count);
        Assert.Equal(1, result.StartupPhases[0].Sequence);
        Assert.Equal(64, result.StartupPhases[^1].Sequence);
        Assert.All(result.StartupPhases, phase => Assert.Equal(StartupPhase.HostComposition, phase.Phase));
    }

    [Fact]
    public void UnknownTextIsOmittedButKnownBuildAndNativeFieldsSurvive()
    {
        var result = SafeBootstrapMetadata.Capture("process.start", "version=1.0.1+abcdef123; arguments=0; os=synthetic private article; architecture=X64");
        Assert.Equal("1.0.1+abcdef123", result.Fields["version"]);
        Assert.Equal("X64", result.Fields["architecture"]);
        Assert.False(result.FieldsComplete);
        Assert.DoesNotContain("synthetic private article", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var native = SafeBootstrapMetadata.Capture("window.opened", "opacity=1; transparency=None; state=Normal");
        Assert.True(native.FieldsComplete);
        Assert.Equal("None", native.Fields["transparency"]);
        Assert.Equal("structured-logging.unavailable", SafeBootstrapMetadata.Capture("structured-logging.unavailable", "untrusted private error prose").EventName);
    }

    [Fact]
    public void ExportRetainsPhaseHistoryOrderAndDurationsWithoutSourceProse()
    {
        var captured = SafeBootstrapMetadata.Capture("startup.phase-summary",
            "sequence=1; phase=HostComposition; state=Started; phaseMs=0; startupMs=3; budget=NotArmed | sequence=2; phase=HostComposition; state=Completed; phaseMs=17; startupMs=20; budget=NotArmed");
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 2,
            Kind = "Bootstrap",
            EventName = captured.EventName,
            SafeFields = captured.Fields,
            captured.FieldsComplete,
            captured.StartupPhases,
            Message = "synthetic private article must not be exported",
        });
        byte[] safe = SafeDiagnosticExportEncoder.Encode(source);
        Assert.DoesNotContain("synthetic private article", Encoding.UTF8.GetString(safe), StringComparison.Ordinal);
        using var output = JsonDocument.Parse(safe);
        var phases = output.RootElement.GetProperty("StartupPhases");
        Assert.Equal(2, phases.GetArrayLength());
        Assert.Equal(1, phases[0].GetProperty("Sequence").GetInt32());
        Assert.Equal(2, phases[1].GetProperty("Sequence").GetInt32());
        Assert.Equal(17, phases[1].GetProperty("PhaseMilliseconds").GetInt64());
        Assert.Equal(20, phases[1].GetProperty("StartupMilliseconds").GetInt64());
        Assert.True(output.RootElement.GetProperty("FieldsComplete").GetBoolean());
    }
}
