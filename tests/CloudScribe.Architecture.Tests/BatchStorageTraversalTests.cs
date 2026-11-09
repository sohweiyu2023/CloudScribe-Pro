#pragma warning disable MA0004 // Test code has no synchronization context to preserve.
using System.Collections;
using System.Reflection;
using Avalonia.Platform.Storage;
using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed class BatchStorageTraversalTests
{
    [Theory]
    [InlineData("content://fixture/root")]
    [InlineData("relative/root")]
    public async Task RecreatedProviderFoldersWithTheSameUriAreNotEnumeratedTwice(string path)
    {
        using var root = new Folder(path);
        using var alias = new Folder(path) { Failure = new InvalidOperationException("A cycle was followed.") };
        using var differentCase = new Folder(path + "/A");
        using var lowerCase = new Folder(path + "/a");
        root.Items = [alias, differentCase, lowerCase];
        var result = await CollectAsync(root, true, TestContext.Current.CancellationToken);
        Assert.Equal(0, result);
        Assert.Equal(1, root.Enumerations);
        Assert.Equal(0, alias.Enumerations);
        Assert.Equal(1, differentCase.Enumerations);
        Assert.Equal(1, lowerCase.Enumerations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsObservedEvenWhenProviderIgnoresItsToken(bool beforeFirstItem)
    {
        using var cancellation = new CancellationTokenSource();
        using var root = new Folder("content://fixture/root");
        using var child = new Folder("content://fixture/child");
        root.Items = [child];
        if (beforeFirstItem) cancellation.Cancel();
        else root.BeforeYield = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(root, true, cancellation.Token));
        Assert.Equal(beforeFirstItem ? 0 : 1, root.Enumerations);
        Assert.Equal(0, child.Enumerations);
    }

    [Fact]
    public async Task UnreadableFolderDoesNotPreventOtherFoldersAndFlatScanDoesNotDescend()
    {
        using var root = new Folder("content://fixture/root");
        using var denied = new Folder("content://fixture/denied") { Failure = new UnauthorizedAccessException() };
        using var missing = new Folder("content://fixture/missing") { Failure = new DirectoryNotFoundException() };
        using var readable = new Folder("content://fixture/readable");
        root.Items = [readable, denied, missing];
        Assert.Equal(0, await CollectAsync(root, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, readable.Enumerations);
        Assert.Equal(2, await CollectAsync(root, true, TestContext.Current.CancellationToken));
        Assert.Equal(1, readable.Enumerations);
    }

    private static Task<int> CollectAsync(Folder root, bool recursive, CancellationToken token)
    {
        var binder = typeof(GoogleTtsUsabilityHotfixBinder);
        var pick = binder.GetNestedType("BatchPick", BindingFlags.NonPublic)!;
        var files = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pick))!;
        var method = binder.GetMethod("CollectTextFilesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task<int>)method.Invoke(null, [root.Storage, root.Name, recursive, files, token])!;
    }

    private sealed class Folder(string path) : IDisposable
    {
        public string Name => Path.OriginalString;
        public Uri Path { get; } = new(path, UriKind.RelativeOrAbsolute);
        public Folder[] Items { get; set; } = [];
        public Action? BeforeYield { get; set; }
        public Exception? Failure { get; init; }
        public int Enumerations { get; private set; }

        public async IAsyncEnumerable<IStorageItem> GetItemsAsync()
        {
            Enumerations++;
            if (Failure is { } error) throw error;
            await Task.CompletedTask;
            foreach (var item in Items) { BeforeYield?.Invoke(); yield return item.Storage; }
        }

        public IStorageFolder Storage
        {
            get
            {
                var proxy = DispatchProxy.Create<IStorageFolder, StorageProxy>();
                ((StorageProxy)(object)proxy).Handler = name => name switch
                {
                    "get_Path" => Path,
                    "get_Name" => Name,
                    "GetItemsAsync" => GetItemsAsync(),
                    _ => throw new NotSupportedException(name)
                };
                return proxy;
            }
        }

        public void Dispose() { }
    }

    // Avalonia marks its storage interfaces as not directly implementable; a runtime proxy models a provider.
    public class StorageProxy : DispatchProxy
    {
        public Func<string, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod!.Name);
    }
}
