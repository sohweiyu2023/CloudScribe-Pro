namespace CloudScribe.Architecture.Tests;

// UI tests share the existing ShellViewModel fixtures. Keep this collection out of
// parallel execution with other architecture tests that construct Avalonia controls
// or inspect process-global diagnostics. Headless services are disposed per case.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CatalogUiTestGroup
{
    public const string Name = "Catalog UI and shell state";
}
