namespace CloudScribe.App.Composition;

public static class CreateMp3NextStep
{
    public static string Describe(bool busy, bool reviewing, bool hasText, bool loading, bool hasVoice,
        bool canPrepare, bool hasReview, bool consent, bool canApprove)
    {
        if (busy) return "Creating your MP3. Wait for the result. If you stop waiting, inspect Setups before retrying; a sent request may still be charged.";
        if (reviewing) return "Checking this exact request and its price. This review does not generate speech.";
        if (!hasText) return "Next: close this window, then write or import text in Studio. You can still browse the public voice samples here.";
        if (loading) return "Next: wait for your saved voices to load. Closing this window stops the wait; no speech is generated.";
        if (!hasVoice) return "Next: load and choose a verified voice above. If the list is empty, read its connection/filter message or clear the voice search.";
        if (!canPrepare) return "Next: wait for the current Google operation to finish before reviewing another request.";
        if (!hasReview) return "Next: choose Review current text, voice and cost. If review fails, read the status below and check pricing or split long text. Review does not create audio.";
        if (!consent) return "Next: read the exact voice and maximum cost, then tick the approval box only if you agree. Creating audio may charge your Google account.";
        if (!canApprove) return "Next: review again because this request or its approval changed. Each request needs fresh consent.";
        return "Ready: Create MP3 sends this exact approved request to Google and may be billed. Click once, then wait for the result.";
    }
}
