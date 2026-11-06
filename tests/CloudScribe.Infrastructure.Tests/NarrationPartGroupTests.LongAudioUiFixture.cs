using System.Text;
using System.Text.Json;
using CloudScribe.Application.Documents;
using CloudScribe.Application.Generation;
using CloudScribe.Infrastructure.Diagnostics;
using CloudScribe.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    // Private native acceptance entry point. Only a synthetic fixture can be supplied; all HTTP remains fake.
    private static async Task<(string App, GoogleLongAudioRequest Request, GoogleLongAudioRuntimeFactory Factory,
        IPrivateProviderErrorPersistence Capture, GoogleTrendHistoryLoggerProvider History)> PrepareLongAdaptiveUiAsync(Fixture f, ChirpEvidence e, DocumentSnapshot document)
    {
        var p = await PrepareLongInstalledAsync(f, e, longDocument: document).ConfigureAwait(false);
        var scope = JsonSerializer.Deserialize<GoogleLongAudioBudgetScope>(File.ReadAllBytes(p.ScopePath))!;
        var root = GoogleLongAudioRequest.Create(p.Request.AttemptId, scope.ProjectNumber, "global", scope.CredentialReferenceId,
            p.Request.OutputGcsUri.Replace(".mp3", ".wav", StringComparison.Ordinal), document.Text, scope.LanguageCode, scope.VoiceName, "LINEAR16");
        var first = GoogleLongAudioRecoveryPlanner.Split(document.Text, new(0, document.Text.EnumerateRunes().Count(), Hash(document.Text), null), root, AdaptiveRejected(root), false, 30);
        var second = GoogleLongAudioRecoveryPlanner.Split(document.Text, first[1].Range, first[1].Request, AdaptiveRejected(first[1].Request), false, 30);
        scope = scope with
        {
            AudioEncoding = "LINEAR16",
            TotalGoogleCapUsdCents = 919,
            ApprovedRequestCeilingsUsdCents = new Dictionary<string, long>(StringComparer.Ordinal) { [root.BindingSha256] = 1 },
            AdaptiveRecoveryRootBindings = [root.BindingSha256],
            DocumentJobs = [scope.DocumentJobs![0] with { OutputGcsUri = root.OutputGcsUri }]
        };
        File.WriteAllBytes(p.ScopePath, JsonSerializer.SerializeToUtf8Bytes(scope)); string sha = Hash(File.ReadAllText(p.ScopePath));
        string install = Path.GetDirectoryName(p.App)!, markerPath = Path.Combine(install, "cloudscribe-install.json");
        var marker = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(markerPath))!; marker["GoogleLongAudioScopeSha256"] = sha;
        File.WriteAllBytes(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker));
        File.WriteAllBytes(p.LedgerPath, JsonSerializer.SerializeToUtf8Bytes(new GoogleLongAudioBudgetLedger(1, sha, [])));
        var parent = JsonSerializer.Deserialize<ChirpArticleAdmissionPolicy>(File.ReadAllBytes(p.ParentPath))!;
        WriteSyntheticLongProvisioningProof(install, parent, scope.ParentPolicySha256, sha, p.LedgerPath, e.Clock.GetUtcNow());
        var history = new GoogleTrendHistoryLoggerProvider(f.Paths, e.Clock);
        var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(history));
        var transport = new AdaptiveTransport([root, first[0].Request, first[1].Request, second[0].Request, second[1].Request],
            [root.AttemptId, first[1].Request.AttemptId], pcm: AdaptivePcm());
        var runtime = await PrepareLongRuntimeAsync(f, e, p.App, root, () => new AdaptiveHandler(transport), new BoundedNativeMediaTool(), logs).ConfigureAwait(false);
        return (p.App, root, runtime.Factory, runtime.Capture, history);
    }
}
