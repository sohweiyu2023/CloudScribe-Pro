using CloudScribe.App.Composition;
using CloudScribe.Domain.Generation;
using CloudScribe.Infrastructure.Generation;

namespace CloudScribe.Architecture.Tests;

public sealed class GoogleVoiceComparisonSelectionTests
{
    [Theory]
    [InlineData("en-US-Neural2-D")]
    [InlineData("en-US-Chirp3-HD-Aoede")]
    public void StudioSelectionDoesNotDependOnShortSampleFamilyOrText(string name)
    {
        var voice = Voice(name);
        var selection = GoogleVoiceComparisonSelection.TryCreate(voice, "en-US");
        Assert.NotNull(selection);
        Assert.Same(voice, selection.Voice);
        Assert.Equal("en-US", selection.LanguageCode);
        Assert.Throws<InvalidOperationException>(() => new GoogleVoiceSampleSpec(voice, "en-US", new string('x', 8166)).Validate());
    }

    [Fact]
    public void ReferenceStaleDisabledUnauthorizedOrMissingLanguageCannotBeSelected()
    {
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(null, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { ProviderStableId = "other-provider" }, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { CapabilityCurrent = false }, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { VoiceEnabled = false }, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { AccountProjectAuthorized = false }, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { LanguageCodes = null }, "en-US"));
        Assert.Null(GoogleVoiceComparisonSelection.TryCreate(Voice() with { VoiceFingerprint = " bad " }, "en-US"));
    }

    [Fact]
    public void SelectionUsesCanonicalProviderLanguageAndNeverInventsOne()
    {
        var voice = Voice() with { LanguageCodes = ["en-US", "en-GB"] };
        Assert.Equal("en-GB", GoogleVoiceComparisonSelection.TryCreate(voice, " EN-gb ")!.LanguageCode);
        Assert.Equal("en-US", GoogleVoiceComparisonSelection.TryCreate(voice, "fr-FR")!.LanguageCode);
    }

    [Fact]
    public void ArticleReviewCannotSilentlyUseADifferentAdmittedVoice()
    {
        var selection = GoogleVoiceComparisonSelection.TryCreate(Voice(), "en-US")!;
        Assert.True(selection.MatchesReviewedVoice("en-US-Neural2-D"));
        Assert.False(selection.MatchesReviewedVoice("en-US-Chirp3-HD-Aoede"));
        Assert.False(selection.MatchesReviewedVoice("cmn-CN-Chirp3-HD-Alnilam"));
        string source = BinderSource("WholeArticle");
        int match = source.IndexOf("ConfirmArticleVoiceSelectionAsync(owner, model, review)", StringComparison.Ordinal);
        int show = source.IndexOf("var dialog = CreateArticleExportDialog();", StringComparison.Ordinal);
        Assert.True(match >= 0 && match < show);
        Assert.Contains("open Library > New document", BinderSource("ArticleVoiceSelection"), StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonNavigationIsSeparateFromSampleValidationAndClearsReviewBeforeDocumentRouting()
    {
        string source = BinderSource("VoiceComparison");
        Assert.Contains("use.IsEnabled = !busy && selected is not null;", source, StringComparison.Ordinal);
        Assert.Contains("GoogleFullDocumentVoiceSupport.Supports(selected.LanguageCode, selected.Voice.VoiceStableId)", source, StringComparison.Ordinal);
        Assert.Contains("reviewDocument.Click += (_, _) => SelectForStudio(review: true);", source, StringComparison.Ordinal);
        Assert.Contains("model.PropertyChanged -= DocumentStateChanged;", source, StringComparison.Ordinal);
        int closeReview = source.LastIndexOf("session.InvalidateReview();", StringComparison.Ordinal);
        int documentReview = source.IndexOf("if (reviewCurrentDocument) await OpenWholeArticleAsync", StringComparison.Ordinal);
        Assert.True(closeReview >= 0 && documentReview > closeReview);
        string planning = BinderSource("ArticlePlanningReview");
        Assert.Contains("GoogleVoiceComparisonInformation.Rate(_comparisonPricing, selection.VoiceStableId)", planning, StringComparison.Ordinal);
        Assert.DoesNotContain("const decimal planningRate = 30m", planning, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonLabelsKeepTheirUtf8Punctuation()
    {
        string source = BinderSource("VoiceComparison");
        Assert.Contains("CloudScribe \u2014 Voices and short samples", source, StringComparison.Ordinal);
        Assert.Contains(" \u00b7 Provider gender label: ", source, StringComparison.Ordinal);
        foreach (string corrupt in new[] { "\u00e2\u20ac", "\u00c2\u00b7", "\ufffd" })
            Assert.DoesNotContain(corrupt, source, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedSetupSelectionUsesRetainedCallbackWhileExplicitRefreshRemainsFull()
    {
        string source = BinderSource();
        int start = source.IndexOf("private Func<Task> AddRefreshControl(", StringComparison.Ordinal);
        int end = source.IndexOf("private void AddVoiceSelector(", start, StringComparison.Ordinal);
        string controls = source[start..end];
        Assert.Contains("return ApplyAsync;", controls, StringComparison.Ordinal);
        Assert.DoesNotContain("return RefreshAsync;", controls, StringComparison.Ordinal);
        Assert.Contains("Task ApplyAsync() => LoadAsync(false);", controls, StringComparison.Ordinal);
        Assert.Contains("Task RefreshAsync() => LoadAsync(true);", controls, StringComparison.Ordinal);
        Assert.Contains("refresh.Click += async (_, _) => await RefreshAsync()", controls, StringComparison.Ordinal);
        Assert.Contains("else await _connectedVoiceCatalog.ApplyFiltersAsync(viewModel, request.Token)", controls, StringComparison.Ordinal);
        string studio = BinderSource("CreateMp3");
        Assert.Contains("reloadVoices = AddRefreshControl(body, model, locale, search, dialog, autoLoad: true);", studio, StringComparison.Ordinal);
        Assert.Contains("if (string.Equals(args.PropertyName, nameof(ShellViewModel.VoiceLabSearchText), StringComparison.Ordinal)) search.Text = model.VoiceLabSearchText;", studio, StringComparison.Ordinal);
        int selection = studio.IndexOf("setups.SelectionChanged +=", StringComparison.Ordinal);
        int apply = studio.IndexOf("model.ApplySavedVoiceSetup(setup);", selection, StringComparison.Ordinal);
        int reload = studio.IndexOf("await reloadVoices()", apply, StringComparison.Ordinal);
        Assert.True(selection >= 0 && apply > selection && reload > apply);
    }

    private static string BinderSource(string? part = null)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudScribe.sln")))
                return File.ReadAllText(Path.Combine(directory.FullName, "src", "CloudScribe.App", "Composition",
                    "GoogleTtsUsabilityHotfixBinder" + (part is null ? "" : "." + part) + ".cs"));
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CloudScribe repository root was not found.");
    }

    private static VoiceLabCatalogSelection Voice(string name = "en-US-Neural2-D") =>
        new(name, GoogleGenerationProvider.StableProviderId, "account", "project", "capability", "fingerprint", true, true, true, ["en-US"]);
}
