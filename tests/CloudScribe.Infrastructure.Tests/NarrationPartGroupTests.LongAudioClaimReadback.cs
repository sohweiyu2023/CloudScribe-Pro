using System.Diagnostics;

namespace CloudScribe.Infrastructure.Tests;

public sealed partial class NarrationPartGroupTests
{
    private static async Task<(int Exit, string Error)> ReadAdaptiveClaimsWithPowerShellAsync(Fixture fixture,
        string app, string scope, string scopeSha, string fault)
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "tools", "long-audio", "Provisioning.Transaction.ps1")))
            repository = Path.GetDirectoryName(repository);
        if (repository is null) throw new InvalidOperationException("Current claim readback source is absent.");
        string script = Path.Combine(fixture.Root, "synthetic-claim-readback.ps1");
        File.WriteAllText(script, """
            param($Repository,$Install,$Profile,$Scope,$Sha,$Fault)
            $ErrorActionPreference='Stop'
            . (Join-Path $Repository 'scripts/Install-CloudScribe.ps1') -CompletionFunctionsOnly
            foreach($name in @('Provisioning.Files.ps1','Provisioning.Scope.ps1','Provisioning.Transaction.ps1')){. (Join-Path $Repository ('tools/long-audio/'+$name))}
            $context=Get-LAContext $Install $Profile $Scope $Sha;Assert-LASavedInputs $context
            $original=Read-LABytes $context.Ledger;$ledger=ConvertFrom-LAJson $original
            switch($Fault){
                'range' {$ledger.Claims[1].Recovery.EndScalarExclusive--}
                'parent' {$ledger.Claims[1].Recovery.ParentCheckpointSha256=('a'*64)}
                'planner' {$ledger.Claims[1].Recovery.PlannerVersion='unreviewed-version'}
                'amount' {$ledger.Claims[1].MaximumUsdCents++}
                'missing' {[void]$ledger.Claims[1].Remove('Recovery')}
                'extra' {$ledger.Claims[1].Recovery.Extra='synthetic-canary'}
                'valid' {}
                default {throw 'UnexpectedSyntheticVector'}
            }
            $context.Ledger=Join-Path $Profile 'synthetic-claim-readback-ledger.json'
            Write-LAAtomic $context.Ledger (ConvertTo-LABytes $ledger) -Replace:(Test-Path -LiteralPath $context.Ledger)
            $sealed=@{Plan=@{NewMarkerSha256=(Get-LAHash (Read-LABytes $context.MarkerPath 16384))}}
            Assert-LACompleted $context $sealed
            '{"SyntheticReadback":true,"ProviderCalls":0}'
            """);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoLogo", "-NoProfile", "-File", script, repository, Path.GetDirectoryName(app)!, fixture.Root, scope, scopeSha, fault })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic claim validator did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        return (process.ExitCode, await stderr.ConfigureAwait(false));
    }
}
