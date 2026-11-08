using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using CloudScribe.App;
using CloudScribe.App.Composition;
using CloudScribe.App.ViewModels;
using CloudScribe.Application.Pricing;
using CloudScribe.Domain.Pricing;
using CloudScribe.Infrastructure.Pricing;

namespace CloudScribe.Architecture.Tests;

[Collection(CatalogUiTestGroup.Name)]
public sealed partial class ShellViewModelStateTests
{
    [Theory]
    [InlineData(false, "button", "success")]
    [InlineData(false, "button", "failure")]
    [InlineData(false, "button", "cancel")]
    [InlineData(false, "escape", "success")]
    [InlineData(false, "escape", "failure")]
    [InlineData(false, "escape", "cancel")]
    [InlineData(false, "window", "success")]
    [InlineData(false, "window", "failure")]
    [InlineData(false, "window", "cancel")]
    [InlineData(true, "button", "success")]
    [InlineData(true, "button", "failure")]
    [InlineData(true, "button", "cancel")]
    [InlineData(true, "escape", "success")]
    [InlineData(true, "escape", "failure")]
    [InlineData(true, "escape", "cancel")]
    [InlineData(true, "window", "success")]
    [InlineData(true, "window", "failure")]
    [InlineData(true, "window", "cancel")]
    public Task CatalogComparisonClosesBeforeUncooperativeResponseAndReopensWithoutDuplicateWork(bool refresh, string dismissal, string outcome) =>
        RunCatalogCloseAsync(refresh, dismissal, outcome, uiQuery: false);

    [Theory]
    [InlineData(false, "button", "success")]
    [InlineData(false, "escape", "cancel")]
    [InlineData(false, "window", "failure")]
    [InlineData(true, "button", "failure")]
    [InlineData(true, "escape", "success")]
    [InlineData(true, "window", "cancel")]
    public Task CatalogComparisonClosesBeforeUncooperativeUiQuery(bool refresh, string dismissal, string outcome) =>
        RunCatalogCloseAsync(refresh, dismissal, outcome, uiQuery: true);

    private static Task RunCatalogCloseAsync(bool refresh, string dismissal, string outcome, bool uiQuery) =>
        OnCatalogUiAsync(async () =>
        {
            var f = await CatalogReopenFixture.CreateAsync().ConfigureAwait(true);
            await using var lifetime = f.ConfigureAwait(true);
            using var model = f.NewModel();
            await f.OpenAsync(model).ConfigureAwait(true);
            using var ui = new CatalogComparisonUi(f, model);
            int writes = f.Vault.Writes, requests = f.Handler.TotalRequests + (uiQuery ? 2 : 0);
            f.Handler.BlockCatalogAt = f.Handler.CatalogRequests + (uiQuery ? 2 : 1);
            f.Handler.IgnoreBlockedCancellation = true;
            f.Handler.BlockedOutcome = outcome;
            Task closed = ui.Open();
            Window first = ui.Dialog;
            int closes = 0;
            first.Closed += (_, _) => closes++;
            ClickCatalog(first, refresh);
            await f.Handler.Entered.Task.ConfigureAwait(true);
            Task idle = CatalogIdleAsync(model);
            Assert.True(CatalogButton(first, "Close voice comparison").IsEnabled);
            DismissCatalog(first, dismissal);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.True(closed.IsCompleted, "Dismissal must not wait for the catalog dependency.");
            await closed.ConfigureAwait(true);
            await f.Handler.CancellationObserved.Task.ConfigureAwait(true);
            Assert.False(f.Handler.Release.Task.IsCompleted); // No timeout/sleep can make this pass.
            Assert.True(model.IsConnectedVoiceCatalogLoading);
            Assert.Equal(1, closes);
            Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
            Assert.Null(model.SelectedVoiceLabVoice);
            Assert.Empty(model.VoiceLabCatalogResults);
            Assert.Equal("en-US", model.VoiceLabLocaleFilter);
            await VerifyCatalogReopenWhileFinishingAsync(ui, f, model, requests, idle).ConfigureAwait(true);
            Assert.Equal(writes, f.Vault.Writes);
            Assert.Equal(0, f.Handler.PaidRequests);
            Assert.Null(model.PreparedGoogleGenerationSpendReview);
            Assert.False(model.PreparedGoogleGenerationSpendApproved);
        });

    private static async Task VerifyCatalogReopenWhileFinishingAsync(CatalogComparisonUi ui, CatalogReopenFixture f,
        ShellViewModel model, int requests, Task idle)
    {
        Task reopened = ui.Open();
        Window second = ui.Dialog;
        Assert.False(CatalogButton(second, "Load connected voices / apply locale").IsEnabled);
        Assert.False(CatalogButton(second, "Refresh connected voices (full verification)").IsEnabled);
        Assert.True(CatalogButton(second, "Close voice comparison").IsEnabled);
        Assert.Equal(requests + 1, f.Handler.TotalRequests);
        model.ApplySavedVoiceSetup(new VoiceSetup("Newer navigation", "cmn-CN", "cmn-CN-Chirp3-HD-Alnilam"));
        string status = model.StatusMessage;
        f.Handler.Release.TrySetResult();
        await idle.ConfigureAwait(true);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Equal("cmn-CN-Chirp3-HD-Alnilam", model.PreferredVoiceName);
        Assert.Equal(status, model.StatusMessage);
        Assert.Null(model.SelectedVoiceLabVoice);
        Assert.Empty(model.VoiceLabCatalogResults);
        Assert.Equal(requests + 1, f.Handler.TotalRequests);
        Assert.True(CatalogButton(second, "Load connected voices / apply locale").IsEnabled);
        DismissCatalog(second, "escape");
        await reopened.ConfigureAwait(true);
        Assert.Equal("cmn-CN", model.VoiceLabLocaleFilter);
        Assert.Same(ui.Invoker, ui.Owner.FocusManager!.GetFocusedElement());
    }

    private static async Task OnCatalogUiAsync(Func<Task> action)
    {
        var originalApplication = Avalonia.Application.Current;
        var session = HeadlessUnitTestSession.StartNew(typeof(CatalogHeadlessApp));
        try
        {
            await session.Dispatch(async () => { await action().ConfigureAwait(true); return true; }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Dispatch completes inline on its own runner. Dispose waits for that runner,
            // so it must be scheduled elsewhere instead of blocking the completion thread.
            await Task.Run(session.Dispose, CancellationToken.None).ConfigureAwait(false);
            Assert.Same(originalApplication, Avalonia.Application.Current);
        }
    }

    private static Button CatalogButton(Window dialog, string label) => Assert.Single(dialog.GetLogicalDescendants()
        .OfType<Button>(), button => string.Equals(Avalonia.Automation.AutomationProperties.GetName(button), label, StringComparison.Ordinal));

    private static void ClickCatalog(Window dialog, bool refresh) => CatalogButton(dialog, refresh
        ? "Refresh connected voices (full verification)" : "Load connected voices / apply locale").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void DismissCatalog(Window dialog, string dismissal)
    {
        if (string.Equals(dismissal, "button", StringComparison.Ordinal))
            CatalogButton(dialog, "Close voice comparison").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else if (string.Equals(dismissal, "escape", StringComparison.Ordinal))
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        else dialog.Close();
    }

    private static Task CatalogIdleAsync(ShellViewModel model)
    {
        if (!model.IsConnectedVoiceCatalogLoading) return Task.CompletedTask;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += Changed;
        return complete.Task;
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not nameof(ShellViewModel.IsConnectedVoiceCatalogLoading) || model.IsConnectedVoiceCatalogLoading) return;
            model.PropertyChanged -= Changed;
            complete.TrySetResult();
        }
    }

    private sealed class CatalogComparisonUi : IDisposable
    {
        private readonly GoogleTtsUsabilityHotfixBinder _binder;
        private readonly ShellViewModel _model;
        private readonly FieldInfo _logDirectory = typeof(CloudScribe.App.Diagnostics.BootstrapDiagnosticLog)
            .GetField("_activeDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previousLogDirectory;
        internal MainWindow Owner { get; }
        internal Button Invoker { get; }
        internal GoogleTtsUsabilityHotfixBinder Binder => _binder;
        internal Window Dialog => Assert.Single(Owner.OwnedWindows);

        internal CatalogComparisonUi(CatalogReopenFixture f, ShellViewModel model)
        {
            _model = model;
            if (!model.CanPrepareGoogleGenerationSpend) ConfigureGoogleSpendReview(model);
            string log = Path.Combine(f.Disk.Paths.RootDirectory, "synthetic-ui-logs");
            Directory.CreateDirectory(log);
            _previousLogDirectory = _logDirectory.GetValue(null);
            _logDirectory.SetValue(null, log); // Never allow MainWindow diagnostics to fall back to a live profile.
            try
            {
                _binder = new(f.Bootstrap, new(new V222ControlSet(new StrictJsonObjectReader()), new NoCatalogPricingAdmission(),
                    new EfPricingCatalogHistoryStore(f.Factory, f.Clock), f.Http), new(Path.Combine(f.Disk.Paths.RootDirectory, "synthetic-audio")),
                    new StudioPreferencesStore(f.Disk.Paths), f.Accounts, f.Disk.Paths,
                    null!, null!, null!, null!, null!, null!, null!);
                Owner = new MainWindow(model);
                Owner.Show();
                Invoker = Owner.FindControl<Button>("VoiceComparisonButton")!;
                Assert.True(Invoker.Focus());
            }
            catch
            {
                _logDirectory.SetValue(null, _previousLogDirectory);
                throw;
            }
        }

        internal Task Open() => (Task)typeof(GoogleTtsUsabilityHotfixBinder)
            .GetMethod("OpenVoiceComparisonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_binder, [Owner, _model])!;

        public void Dispose()
        {
            try { Owner.Close(); }
            finally { _logDirectory.SetValue(null, _previousLogDirectory); }
        }
    }

    private sealed class NoCatalogPricingAdmission : IPricingCatalogAdmissionService
    {
        public PricingCatalogDryRunResult DryRun(ReadOnlyMemory<byte> utf8Catalog, PricingCatalogSignature? signature = null) =>
            throw new InvalidOperationException("Catalog interaction must not activate pricing.");
    }
}
