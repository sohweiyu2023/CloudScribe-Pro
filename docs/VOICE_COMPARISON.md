# Compare voices and narrate the same text

Choose **Voices** in the top toolbar. The comparison window shows the voice used for the existing AtR recordings (`en-US-Standard-C`), the current Studio voice, suggested alternatives, character prices, language/locale, provider gender metadata when returned, and brief family descriptions. Voice preference and pronunciation need listening comparison; a more expensive family is not a promise of a better result for your text.

The **Official voice samples & catalog** link opens Google's prerecorded examples. The **Official pricing** link opens the source for the list-price reference. Standard and WaveNet are USD 4 per million characters in the official reference checked on 20 October 2026; Neural2 is USD 16. These rates also match the application's authenticated v2.22 catalog. Free allowances, taxes and currency conversion are not assumed. Unknown rates are not invented.

## Discover and compare

1. Set an English locale such as `en-US`, `en-GB`, `en-AU` or `en-IN`.
2. Choose **Load connected voices / apply locale**. Normal loading and filter changes check the saved connection while retaining matching, unexpired capability identity and its original expiry. **Refresh connected voices (full verification)** is a separate explicit refresh that can replace verification evidence and invalidate an earlier exact review. Neither action generates audio or changes credentials.
3. Filter the names or families and select a voice. Suggested entries that have not been returned for the connected account are clearly **reference only**. The provider's gender label is displayed when available, not guessed from the final letter of a voice name.
4. The list and details show the authenticated catalog rate, a short-sample estimate and a planning estimate for the current document. Planning estimates do not approve or send anything. Actual sample preparation independently rechecks current pricing, account/project authorization, capabilities, exact compiled bytes and queue state.

**Use selected voice in Studio** works independently of the sample text and short-sample family. A connected, current catalog voice can be selected even when the sample box is empty or longer than 500 characters, or when that voice uses a different generation route. Selecting a voice does not approve or send speech.

The short MP3 sample flow is intentionally limited to Standard, WaveNet and Neural2. Chirp 3 HD uses the full-document route below rather than this short-sample box. Other catalog families remain visible with official sample links. A catalog name alone does not assert that its request settings, active pricing or synthesis capabilities are usable. The existing generation gates remain authoritative.

## Compare a longer saved document

1. Save the common comparison passage as a **separate Studio document for each voice**, with distinct document identities and the same exact text. Copy the Studio editor text, open **Library > New document**, paste it, give it a different title, and press **Ctrl+S**. For example, compare `en-US-Neural2-D` with `en-US-Chirp3-HD-Aoede` only when those exact voices are in the current connected catalog. A passage intended to last about ten minutes may have different durations in the two voices.
2. Open **Voices**, load the connected catalog and choose the voice. Use explicit refresh only when needed; expired or changed evidence requires fresh review rather than reuse of old approval.
3. Choose **Review full document with this voice**. Neural2 and Chirp 3 HD use their supported full-document planner, which splits losslessly at internal request boundaries and assembles one MP3. The 500-character short-sample box is not used.
4. Review the exact document, voice, saved audio, request count and cost. Each document needs its own valid installed admission and exact approval. Choosing this action does not itself reserve money or send speech.
5. For the other voice, open its separate saved document and repeat the review. Do not change an existing document's authority or regenerate its saved audio just to compare voices.

The action needs a saved, nonempty document with no unsaved edits and a current supported connected voice. An absent or stale admission remains a readable blocker. If the selected voice differs from the document's admitted voice, **Choose a separate document for this voice** explains the mismatch and returns you to Studio. Selecting a different voice never replaces prior authority or discards earlier jobs.

Neural2 uses planner version `google-neural2-compiled-boundaries-v1`: plain-text MP3 requests are packed against the actual serialized envelope, at most 5,000 bytes including JSON escaping and request fields. Existing natural boundaries are preferred; a long uninterrupted token can split only between complete Unicode text elements. Text, whitespace, ordering and the final cutoff are preserved. An indivisible text element or whitespace-only range that cannot fit fails closed, without trimming or silently changing the text.

Existing Chirp plans keep their planner versions, hashes and boundary rules. The legacy `chirp3-existing-boundaries-v1` planner retains its 333-scalar window, which is a local heuristic rather than Google's documented sentence limit. Ordinary batch Chirp planning retains its separate sentence plan. Neural2 does not inherit that 333-scalar heuristic.

Full-document comparison adds no arbitrary short-document limit. It retains the existing resource bounds of 1,000,000 Unicode scalars per source and 10,000 coverage parts, plus per-request byte checks, provider restrictions, admission expiry and approved spending caps. This is not a promise of unlimited input or guaranteed provider acceptance.

## Generate one short sample only after reviewing its cost

1. Enter a common sample of **1 to 500 characters**, or use the supplied short comparison passage. This box is separate from the document. The document text is never substituted, edited or sent by this flow.
2. Choose **Prepare sample and show exact price**. No synthesis occurs. Read the exact USD request ceiling, voice and pricing provenance.
3. Check **I approve this exact sample and its displayed USD request ceiling**, then choose **Generate sample**. This is the only action that submits a potentially billed sample, using the existing Stage6 single-use spend approval and reconciliation gates.
4. Playback is manual. Choose **Replay matching sample — USD 0** or **Export matching sample MP3** after a verified result exists.

Changing the voice or text clears the sample review and checkbox. Closing the comparison clears unused sample approval. The prior Studio voice/filter and document output selection are restored unless **Use selected voice in Studio** or **Review full document with this voice** was explicitly chosen. The sample itself does not replace the document's selected audio output.

## Cache and failures

The new cache is **session-only** and contains only references to MP3s accepted and integrity-verified by the existing Stage6 output service. It is separate from the generic Stage7 audition/cache path, which remains fresh-only because it has no verified production cache-reader binding and the Google adapter supplies catalog discovery rather than its generic audition interface.

Cache identity includes provider, account, project, exact voice fingerprint, language, exact text and fixed MP3/rate/pitch/volume settings. Replay and export verify the file's length and SHA-256 against the accepted bytes held in memory before use. They never call prepare, approve or generate. A changed, missing or unmatched file cannot be replayed as a trusted sample. Restarting the app clears this cache; no persistent or unverified cache is silently adopted.

Failures after a possible submission are not automatically retried or cached. The same session marks that sample as requiring queue review; the durable Stage6 queue and reconciliation gates are retained. No interface claims that a failed or cancelled request is necessarily free.

## Verification scope

Feature development uses mocked provider submissions and local/existing audio fixtures only. A build or test does not authorize a billed sample. Existing full article transcripts, review drafts and narration MP3s are not part of this feature's source publication and are not regenerated by it.

Sources: [Google voice catalog and samples](https://docs.cloud.google.com/text-to-speech/docs/list-voices-and-types), [Google pricing](https://cloud.google.com/text-to-speech/pricing). The checked reference date is displayed explicitly rather than described as a live price lookup. The current active pricing gate must still pass before any sample can be approved.
