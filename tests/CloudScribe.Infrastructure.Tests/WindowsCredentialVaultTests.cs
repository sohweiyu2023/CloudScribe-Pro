using CloudScribe.Application.Security;
using CloudScribe.Infrastructure.Security;
using CloudScribe.Providers.Abstractions;

namespace CloudScribe.Infrastructure.Tests;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public async Task WindowsCredentialManagerRoundTripsAndDeletesEphemeralSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsCredentialVault vault = new();
        CredentialReference reference = new($"test.{Guid.NewGuid():N}");
        char[] secret = "stage4-ephemeral-test-secret".ToCharArray();
        try
        {
            await vault.StoreAsync(reference, secret, TestContext.Current.CancellationToken);
            using CredentialSecret? loaded = await vault.ReadAsync(reference, TestContext.Current.CancellationToken);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Value.Span.SequenceEqual(secret));
            Assert.True(await vault.DeleteAsync(reference, TestContext.Current.CancellationToken));
            Assert.Null(await vault.ReadAsync(reference, TestContext.Current.CancellationToken));
        }
        finally
        {
            Array.Clear(secret);
            _ = await vault.DeleteAsync(reference, TestContext.Current.CancellationToken);
        }
    }
}
