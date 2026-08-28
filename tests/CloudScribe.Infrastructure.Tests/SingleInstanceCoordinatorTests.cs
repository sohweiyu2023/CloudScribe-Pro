using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using CloudScribe.Application.Activation;
using CloudScribe.Infrastructure.Activation;
using CloudScribe.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudScribe.Infrastructure.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void ActivationPayloadRequiresUtcAndNonNullArguments()
    {
        Assert.Throws<ArgumentException>(() =>
            new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, ["file.txt"], new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() =>
            new ActivationReceivedEventArgs(ActivationSource.SecondaryInstance, new string[] { null! }, DateTimeOffset.UtcNow));
    }


    [Fact]
    public async Task PrimaryStartupActivationIsRetainedUntilShellSubscribes()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        ActivationRouter router = new();
        SingleInstanceCoordinator primary = CreateCoordinator(paths, router);

        try
        {
            Assert.True(await primary.TryBecomePrimaryAsync(["initial.txt"], TestContext.Current.CancellationToken));
            ActivationReceivedEventArgs? received = null;

            router.ActivationReceived += (_, eventArgs) => received = eventArgs;

            Assert.NotNull(received);
            Assert.Equal(ActivationSource.PrimaryLaunch, received.Source);
            Assert.Collection(
                received.Arguments,
                argument => Assert.Equal("initial.txt", argument));
        }
        finally
        {
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task PrimaryStartupRejectsOversizedActivationBeforeTakingTheLock()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, new ActivationRouter());

        try
        {
            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                () => primary.TryBecomePrimaryAsync([new string('x', 9000)], TestContext.Current.CancellationToken));

            Assert.Contains("UTF-8 bytes", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(paths.InstanceLockPath));
        }
        finally
        {
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task SecondaryInstanceRoutesBoundedActivationToPrimary()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        ActivationRouter primaryRouter = new();
        TaskCompletionSource<ActivationReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        primaryRouter.ActivationReceived += (_, eventArgs) => received.TrySetResult(eventArgs);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, primaryRouter);
        SingleInstanceCoordinator secondary = CreateCoordinator(paths, new ActivationRouter());

        try
        {
            bool becamePrimary = await primary.TryBecomePrimaryAsync([], TestContext.Current.CancellationToken);
            bool secondaryBecamePrimary = await secondary.TryBecomePrimaryAsync(["sample.txt"], TestContext.Current.CancellationToken);
            ActivationReceivedEventArgs routed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(becamePrimary);
            Assert.False(secondaryBecamePrimary);
            Assert.Equal(ActivationSource.SecondaryInstance, routed.Source);
            Assert.Collection(routed.Arguments, argument => Assert.Equal("sample.txt", argument));
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task SecondaryInstanceRejectsTooManyActivationArgumentsInsteadOfDroppingThem()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, new ActivationRouter());
        SingleInstanceCoordinator secondary = CreateCoordinator(paths, new ActivationRouter());

        try
        {
            Assert.True(await primary.TryBecomePrimaryAsync([], TestContext.Current.CancellationToken));
            string[] arguments = Enumerable.Range(0, 65).Select(index => $"file-{index}.txt").ToArray();

            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                () => secondary.TryBecomePrimaryAsync(arguments, TestContext.Current.CancellationToken));

            Assert.Contains("maximum is 64", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task SecondaryInstanceRejectsOversizedActivationPayloadInsteadOfSendingEmptyArguments()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, new ActivationRouter());
        SingleInstanceCoordinator secondary = CreateCoordinator(paths, new ActivationRouter());

        try
        {
            Assert.True(await primary.TryBecomePrimaryAsync([], TestContext.Current.CancellationToken));

            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                () => secondary.TryBecomePrimaryAsync([new string('x', 9000)], TestContext.Current.CancellationToken));

            Assert.Contains("UTF-8 bytes", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task StalledSecondaryConnectionCannotMonopolizePrimaryListener()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, new ActivationRouter());
        SingleInstanceCoordinator secondary = CreateCoordinator(paths, new ActivationRouter());
        NamedPipeClientStream stalled = new(
            ".",
            PipeNameFor(paths.RootDirectory),
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            Assert.True(await primary.TryBecomePrimaryAsync([], TestContext.Current.CancellationToken));
            await stalled.ConnectAsync(TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Task<bool> activation = secondary.TryBecomePrimaryAsync(["after-stall.txt"], TestContext.Current.CancellationToken);
            bool secondaryBecamePrimary = await activation.WaitAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

            Assert.False(secondaryBecamePrimary);
        }
        finally
        {
            await stalled.DisposeAsync();
            await secondary.DisposeAsync();
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task ThrowingActivationSubscriberDoesNotTerminateListener()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        ActivationRouter router = new();
        TaskCompletionSource firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ActivationReceivedEventArgs> throwing = (_, _) =>
        {
            firstAttempt.TrySetResult();
            throw new InvalidOperationException("subscriber failure");
        };
        router.ActivationReceived += throwing;
        TaskCompletionSource<ActivationReceivedEventArgs> firstHealthy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        router.ActivationReceived += (_, eventArgs) => firstHealthy.TrySetResult(eventArgs);
        SingleInstanceCoordinator primary = CreateCoordinator(paths, router);
        SingleInstanceCoordinator firstSecondary = CreateCoordinator(paths, new ActivationRouter());
        SingleInstanceCoordinator secondSecondary = CreateCoordinator(paths, new ActivationRouter());

        try
        {
            Assert.True(await primary.TryBecomePrimaryAsync([], TestContext.Current.CancellationToken));
            Assert.False(await firstSecondary.TryBecomePrimaryAsync(["first.txt"], TestContext.Current.CancellationToken));
            await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            ActivationReceivedEventArgs firstRouted = await firstHealthy.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Collection(firstRouted.Arguments, argument => Assert.Equal("first.txt", argument));
            router.ActivationReceived -= throwing;

            TaskCompletionSource<ActivationReceivedEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            router.ActivationReceived += (_, eventArgs) => received.TrySetResult(eventArgs);
            Assert.False(await secondSecondary.TryBecomePrimaryAsync(["second.txt"], TestContext.Current.CancellationToken));
            ActivationReceivedEventArgs routed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Collection(routed.Arguments, argument => Assert.Equal("second.txt", argument));
        }
        finally
        {
            await secondSecondary.DisposeAsync();
            await firstSecondary.DisposeAsync();
            await primary.DisposeAsync();
            DeleteDirectoryBestEffort(root);
        }
    }


    [Fact]
    public async Task AsyncDisposeReleasesInstanceLockAfterNonFatalListenerFault()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        paths.EnsureRootDirectory();
        SingleInstanceCoordinator coordinator = CreateCoordinator(paths, new ActivationRouter());
        FileStream lockStream = OpenExclusiveLock(paths.InstanceLockPath);
        SetPrivateField(coordinator, "_lockStream", lockStream);
        SetPrivateField(coordinator, "_listenerTask", Task.FromException(new IOException("listener failed")));

        await coordinator.DisposeAsync();

        Assert.Null(GetPrivateField<FileStream>(coordinator, "_lockStream"));
        using (FileStream reopened = OpenExclusiveLock(paths.InstanceLockPath))
        {
        }
        DeleteDirectoryBestEffort(root);
    }

    [Fact]
    public void DisposeReleasesInstanceLockAfterNonFatalListenerFault()
    {
        string root = CreateTestRoot();
        AppPaths paths = CreatePaths(root);
        paths.EnsureRootDirectory();
        SingleInstanceCoordinator coordinator = CreateCoordinator(paths, new ActivationRouter());
        FileStream lockStream = OpenExclusiveLock(paths.InstanceLockPath);
        SetPrivateField(coordinator, "_lockStream", lockStream);
        SetPrivateField(coordinator, "_listenerTask", Task.FromException(new IOException("listener failed")));

        coordinator.Dispose();

        Assert.Null(GetPrivateField<FileStream>(coordinator, "_lockStream"));
        using (FileStream reopened = OpenExclusiveLock(paths.InstanceLockPath))
        {
        }
        DeleteDirectoryBestEffort(root);
    }

    private static string CreateTestRoot() =>
        Path.Combine(Path.GetTempPath(), "cloudscribe-tests", Guid.NewGuid().ToString("N"));

    private static AppPaths CreatePaths(string root) =>
        new(Options.Create(new CloudScribeOptions { AppDataDirectoryOverride = root }));

    private static void DeleteDirectoryBestEffort(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string PipeNameFor(string rootDirectory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rootDirectory)));
        return "cloudscribe-pro-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }


    private static FileStream OpenExclusiveLock(string path) => new(
        path,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private static void SetPrivateField<T>(SingleInstanceCoordinator coordinator, string name, T value)
    {
        System.Reflection.FieldInfo field = typeof(SingleInstanceCoordinator).GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field: {name}");
        field.SetValue(coordinator, value);
    }

    private static T? GetPrivateField<T>(SingleInstanceCoordinator coordinator, string name)
        where T : class
    {
        System.Reflection.FieldInfo field = typeof(SingleInstanceCoordinator).GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test field: {name}");
        return field.GetValue(coordinator) as T;
    }

    private static SingleInstanceCoordinator CreateCoordinator(AppPaths paths, IActivationRouter router) => new(
        paths,
        router,
        TimeProvider.System,
        NullLogger<SingleInstanceCoordinator>.Instance);
}
