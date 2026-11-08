using System.Reflection;
using System.Runtime.CompilerServices;
using CloudScribe.App.Composition;
using CloudScribe.App.Navigation;
using CloudScribe.App.ViewModels;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Fact]
    public void QueueEntryVisibilityFollowsNavigationAndKeepsInspectionContent()
    {
        using ShellViewModel model = CreateViewModel();
        model.ApplyFinalReleasePresentation();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.SelectedNavigationItem = model.NavigationItems.Single(x => x.Route == AppRoute.Queue);
        Assert.True(model.IsQueueSelected);
        Assert.Contains(nameof(ShellViewModel.IsQueueSelected), changed, StringComparer.Ordinal);
        Assert.Contains("persisted request counts", model.CurrentPage.StateDescription, StringComparison.Ordinal);
        model.SelectedNavigationItem = model.NavigationItems.Single(x => x.Route == AppRoute.Studio);
        Assert.False(model.IsQueueSelected);
    }

    [Fact]
    public async Task RepeatedBatchOpenIsRefusedBeforeTouchingAnyProfileOrProvider()
    {
        using ShellViewModel model = CreateViewModel();
        var type = typeof(GoogleTtsUsabilityHotfixBinder);
        // No services are configured: a repeated open must return before any dependency is used.
        object binder = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_batchDialogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(binder, true);
        MethodInfo open = type.GetMethod("OpenBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 2; i++)
        {
            var task = Assert.IsAssignableFrom<Task>(open.Invoke(binder, [null, model, null, false]));
            await task.ConfigureAwait(true);
            Assert.Contains("already opening or open", model.StatusMessage, StringComparison.Ordinal);
        }
        Assert.True((bool)type.GetField("_batchDialogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binder)!);
        Assert.False(model.CanGenerateWithGoogle);
    }
}
