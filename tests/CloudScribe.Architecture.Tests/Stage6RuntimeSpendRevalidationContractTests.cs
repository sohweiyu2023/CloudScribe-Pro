namespace CloudScribe.Architecture.Tests;

public sealed class Stage6RuntimeSpendRevalidationContractTests
{
    [Fact]
    public void ProductionRuntimeRevalidatesDurableSpendAgainstCurrentRequestValues()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CloudScribe.App",
            "Composition",
            "GoogleGenerationProductionRuntimeEvidenceResolver.cs"));

        string[] requiredCurrentRequestBindings =
        [
            "request.SubmissionEnvelope,",
            "request.Currency,",
            "request.Scale,",
            "request.CurrentEstimateMinorUnits,",
        ];

        foreach (string required in requiredCurrentRequestBindings)
        {
            Assert.True(source.Contains(required, StringComparison.Ordinal),
                $"Stage6 runtime spend authorization must remain bound to current request evidence: {required}");
        }

        Assert.False(source.Contains(
            "spendAuthorization.Currency,\n            spendAuthorization.Scale,\n            spendAuthorization.ApprovedEstimateMinorUnits",
            StringComparison.Ordinal),
            "Stage6 runtime must not validate durable spend authorization only against its own stored monetary values.");

        int durableResolve = source.IndexOf(
            ".ResolveAsync(\n                request.SubmissionEnvelope,\n                request.Currency,\n                request.Scale,\n                request.CurrentEstimateMinorUnits,",
            StringComparison.Ordinal);
        int runtimeEvidence = source.IndexOf(
            "return new GoogleGenerationAuthorizedRuntimeEvidence(",
            StringComparison.Ordinal);
        Assert.True(durableResolve >= 0 && runtimeEvidence > durableResolve,
            "Stage6 must revalidate durable spend against current runtime values before emitting authorized runtime evidence.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln"))
                && File.Exists(Path.Combine(directory.FullName, "SESSION_STATE.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the CloudScribe repository root from the test working directory.");
    }
}
