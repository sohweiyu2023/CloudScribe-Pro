using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class DesktopApartmentTests
{
    [Fact]
    public async Task AsyncStartupUsesStaForWindowsDesktop()
    {
        int result = await Task.Run(() => DesktopApartment.RunAsync(() =>
        {
            if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.Equal("CloudScribe desktop", Thread.CurrentThread.Name);
            return 17;
        }), TestContext.Current.CancellationToken);
        Assert.Equal(17, result);
    }

    [Fact]
    public async Task StartupFailureReachesCaller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopApartment.RunAsync(() => throw new InvalidOperationException("fixture")));
    }
}
