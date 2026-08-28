using System.Reflection;

namespace CloudScribe.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void DomainHasNoForbiddenReferences()
    {
        Assembly domain = typeof(CloudScribe.Domain.Observability.ExactMoney).Assembly;
        string[] references = domain.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("CloudScribe.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("CloudScribe.App", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationDoesNotReferencePresentationOrInfrastructure()
    {
        Assembly application = typeof(CloudScribe.Application.Activation.ActivationRouter).Assembly;
        string[] references = application.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToArray();

        Assert.DoesNotContain("CloudScribe.App", references);
        Assert.DoesNotContain("CloudScribe.Infrastructure", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderAbstractionsDoNotReferenceConcreteAdapters()
    {
        Assembly abstractions = typeof(CloudScribe.Providers.Abstractions.IProviderAdapterFactory).Assembly;
        Assert.DoesNotContain(
            abstractions.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("CloudScribe.Providers.", StringComparison.Ordinal) == true &&
                         !string.Equals(reference.Name, "CloudScribe.Providers.Abstractions", StringComparison.Ordinal));
    }
}
