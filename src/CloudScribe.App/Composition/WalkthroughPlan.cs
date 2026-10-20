namespace CloudScribe.App.Composition;

/// <summary>Only navigation and instructional text. No commands, credentials or request approval.</summary>
public static class WalkthroughPlan
{
    public static IReadOnlyList<WalkthroughStep> Build(bool savedConnection)
    {
        List<WalkthroughStep> steps =
        [
            new(WalkthroughScreen.Studio, "DocumentEditor|Create a new local document from empty workspace",
                "Start with a short paragraph", "This outline marks the real control. If the page is empty, choose New document, then type or paste two sentences in the editor. Your text saves locally. You can do the step now, or use Next to look ahead. Tab moves between controls; Escape skips the guide."),
            new(WalkthroughScreen.Studio, "CreateMp3Button", "This opens your MP3 workspace",
                "Create MP3 is where you choose a voice and check the price. Next opens the relevant screen for you. The guide never clicks a billable control or ticks approval for you."),
        ];
        if (!savedConnection)
        {
            steps.Add(new(WalkthroughScreen.Connection, "GoogleProjectIllustration", "1. Prepare your Google project",
                "In your browser, choose or create a Google Cloud project, link billing you control, then enable the Cloud Text-to-Speech API. Keep the same project selected throughout setup. The numbered illustration shows the route; it is not a screenshot of your account. Next stays in CloudScribe.",
                "https://cloud.google.com/text-to-speech/docs/before-you-begin"));
            steps.Add(new(WalkthroughScreen.Connection, "GoogleKeyIllustration", "2. Get a service-account JSON file",
                "In Google Cloud, open IAM & Admin, then Service Accounts. Create or select a service account authorized for Text-to-Speech in your project. Open that account's Keys tab, choose Add key, Create new key, JSON, then Create. Keep the downloaded file private. If key creation is blocked, ask your project administrator; do not bypass organization policy.",
                "https://cloud.google.com/iam/docs/keys-create-delete"));
            steps.Add(new(WalkthroughScreen.Connection, "Service-account JSON", "3. Connect CloudScribe once",
                "Open your downloaded JSON file in a text editor and copy its complete contents into the highlighted field. Keep the suggested local labels and Google URLs above it for a first connection. These labels are not your Google project ID. Do not put the JSON in your document, a screenshot or a message."));
            steps.Add(new(WalkthroughScreen.Connection, "Configure Google TTS securely", "4. Save and check the connection",
                "Choose Configure Google TTS securely yourself when ready. It checks Google access and stores the key in Windows Credential Manager; the input clears afterwards. Read the status below. No speech is generated. If a local name is already used, inspect Setups before changing connection labels. Next can preview the remaining steps even without an account."));
        }
        steps.AddRange(
        [
            new(WalkthroughScreen.Create, "GuideConnectionStatus", savedConnection ? "Your saved connection is ready to check" : "Check your connection status",
                savedConnection ? "CloudScribe found one enabled Google connection. Keep it: you do not need to paste your key again. You may choose a saved voice setup below. A remembered voice still needs a fresh catalog check. If access fails, close this screen and inspect Setups or Advanced tools."
                : "Read the connection status here. If setup is incomplete, Back returns to the connection form. You can still explore the remaining steps. A missing connection prevents generation; the guide does not create one automatically."),
            new(WalkthroughScreen.Create, "Refresh real Google voices", "Refresh the available voices",
                "Choose Refresh real Google voices to check access and load the voices your account can use. This does not generate speech. For another language, expand Language and voice filters first; for example en-US means US English. If the list is empty, clear the search and check the connection. Access checks expire, so refresh when prompted."),
            new(WalkthroughScreen.Create, "Voice", "Choose a voice from this list",
                "Open the highlighted Voice list and select a returned voice. The name includes its language and voice family. Start with a Standard voice if suitable, then check its actual price in the next step. A remembered name alone is not verification. Voice samples elsewhere in the app can be billable."),
            new(WalkthroughScreen.Create, "Review pricing source if pricing is unavailable", "Check the price source",
                "If pricing is unavailable, open this section. Follow the official pricing link, compare the voice family's rates with the included catalog, then tick the match box only if they agree and choose Fetch current pricing evidence and activate. Fetching does not compare rates for you. Never confirm a mismatch.",
                "https://cloud.google.com/text-to-speech/pricing"),
            new(WalkthroughScreen.Create, "Review current text, voice and cost", "Review this exact paragraph and voice",
                "Choose Review current text, voice and cost. Read the estimate below. This prepares a local review without generating audio. If the text is too long, return to Studio and use Advanced tools to create local parts; each part needs separate review and approval."),
            new(WalkthroughScreen.Create, "I approve this exact request and its displayed maximum cost.", "Read the maximum before approving",
                "Read the exact voice, currency and maximum estimate above this box. Tick it yourself only if you accept this request. Editing text or changing the voice clears the review. Taxes, credits and conversion can change your Google bill. The guide never checks this box."),
            new(WalkthroughScreen.Create, "GuideGenerateButton", "Create once when you are ready",
                "This is the billable button. Choose it yourself only after review and approval. It sends your document text to Google and may charge your account. Wait for the status. If the result is uncertain, inspect local request status in Setups before retrying; stopping the wait does not cancel a possible charge. Next only continues the guide."),
            new(WalkthroughScreen.Create, "Listen, save and find audio", "Listen, export and find your file",
                "This section opens for you. After an accepted MP3 exists, use its play and export controls. Without one, playback/export stays unavailable. Open saved MP3 folder finds files on disk. After restart, use those files in your normal audio player; in-app verified replay is session-only."),
            new(WalkthroughScreen.Return, "WorkspaceSettingsButton", "Come back with your work intact",
                "Setups holds saved voices and local request status. Your document, connection and preferences survive restart; refresh access and approve each new request. Help replays this walkthrough, including an optional Google setup route. Give friends the clean distribution ZIP; keep your profile, keys, documents and MP3s private."),
        ]);
        return steps;
    }
}
