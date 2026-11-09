using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CloudScribe.App.Composition;

namespace CloudScribe.Architecture.Tests;

public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(480)]
    [InlineData(1360)]
    public Task FirstRunInvitationIsOptionalAndDismissalSurvivesRestart(int width) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        ui.Owner.Width = width;
        ui.Owner.Height = 520;
        ui.Owner.UpdateCommandLayout(width);
        var focused = ui.Owner.FocusManager!.GetFocusedElement();
        MountInvitation(ui, model);
        ui.Owner.UpdateLayout();
        var card = ui.Owner.FindControl<Border>("FirstRunWelcomeCard")!;
        Assert.True(card.IsVisible);
        Assert.Same(focused, ui.Owner.FocusManager.GetFocusedElement());
        Assert.Empty(ui.Owner.OwnedWindows);
        Assert.DoesNotContain(ui.Owner.GetVisualDescendants().OfType<TextBlock>(), IsGuideProgress);
        var start = ui.Owner.FindControl<Button>("StartTutorialButton")!;
        var dismiss = ui.Owner.FindControl<Button>("DismissTutorialButton")!;
        Assert.True(start.IsEffectivelyVisible);
        Assert.True(dismiss.IsEffectivelyVisible);
        Assert.True(start.Bounds.Width > 0);
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(card.IsVisible);
        Assert.Same(ui.Owner.FindControl<Button>("QuickStartHelpButton"), ui.Owner.FocusManager.GetFocusedElement());
        Assert.True(new StudioPreferencesStore(f.Disk.Paths).State.QuickStartSeen);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Fact]
    public Task TutorialCanBeSkippedAndReplayedWithoutAutomaticProviderActions() => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        MountInvitation(ui, model);
        var start = ui.Owner.FindControl<Button>("StartTutorialButton")!;
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(ui.Owner.FindControl<Border>("FirstRunWelcomeCard")!.IsVisible);
        Assert.Single(ui.Owner.GetVisualDescendants().OfType<TextBlock>(), IsGuideProgress);
        ui.Owner.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.DoesNotContain(ui.Owner.GetVisualDescendants().OfType<TextBlock>(), IsGuideProgress);
        Task replay = (Task)typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("OpenQuickStartObservedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ui.Binder, [ui.Owner, model])!;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Single(ui.Owner.GetVisualDescendants().OfType<TextBlock>(), IsGuideProgress);
        ui.Owner.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        await replay.ConfigureAwait(true);
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    [Theory]
    [InlineData("{\"Version\":1,\"QuickStartSeen\":false}")]
    [InlineData("{\"Version\":1,\"QuickStartSeen\":true}")]
    [InlineData("unreadable existing preferences")]
    public Task ExistingProfilesNeverGetAnAutomaticInvitation(string original) => OnCatalogUiAsync(async () =>
    {
        var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
        await using var lifetime = f.ConfigureAwait(true);
        string path = Path.Combine(f.Disk.Paths.RootDirectory, "studio-preferences.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var model = f.NewModel();
        using var ui = new CatalogComparisonUi(f, model);
        MountInvitation(ui, model);
        Assert.False(ui.Owner.FindControl<Border>("FirstRunWelcomeCard")!.IsVisible);
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(0, f.Handler.PaidRequests);
    });

    private static bool IsGuideProgress(TextBlock text) => text.Text?.StartsWith("GUIDED WALKTHROUGH", StringComparison.Ordinal) == true;

    private static void MountInvitation(CatalogComparisonUi ui, CloudScribe.App.ViewModels.ShellViewModel model) =>
        typeof(GoogleTtsUsabilityHotfixBinder).GetMethod("MountFirstRunInvitation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ui.Binder, [ui.Owner, model]);
}
