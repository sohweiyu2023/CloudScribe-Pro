using CloudScribe.Application.Generation;

namespace CloudScribe.App.ViewModels;

public sealed record VoiceLabCatalogUiState(
    VoiceLabCatalogQuery Query,
    bool AccountAuthorized,
    bool ProjectAuthorized,
    bool PrivateVoiceAccessAuthorized);
