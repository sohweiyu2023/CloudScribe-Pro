# Saved setups, autosave and local recovery

This local update builds on main `542fc32ce4873ac6332c8c2ef55a7d17f2f92e04`. It does not change the Google credential boundary or authorize provider spending.

## Everyday use

1. Launch the supplied updated launcher. Reopen a document in **Library**, or use **Ctrl+N** / **Ctrl+O** to create/import one. Text and title autosave after about 750 ms; **Ctrl+S** creates a checkpoint. Wait for the saved state before ending a session.
2. If Google is already connected, skip **Connect Google**. Open the inspector with **Ctrl+Shift+I**, enter the language, and use **Refresh real Google voices**. The saved connection is reused; access/capability evidence must still be refreshed when stale.
3. Select a returned voice. Its voice/language choice is remembered automatically. To name it, close an inspector drawer if necessary, choose **Setups**, enter a name and choose **Save current voice setup**.
4. In a later session, **Setups → Load selected setup** restores only intent. Close the dialog and refresh voices. The exact matching returned voice is selected; a missing preferred voice is not silently replaced with a different one. Voice search remains a temporary filter.
5. Inspect stored pricing status and review current official prices when necessary. The official pricing URL is supplied, but the rate-match checkbox is never selected automatically. Fetching an HTTPS page does not verify its rates.
6. Prepare the exact request, inspect its estimate, explicitly approve the spending limit, then generate once. Changing text, document, voice or locale clears the old review and approval, including changes while preparation/approval is running.
7. Play/export the accepted MP3 in the current session. After restart, choose **Audio → Open saved MP3 folder**, or use the folder button in **Setups**, to listen to existing files with your normal player. Listening does not require regeneration.

The current compiler sends plain text inside JSON. It does not interpret pasted SSML as a rich narration plan. The conservative compiled-envelope limit includes JSON escaping and request fields; visible character count alone is insufficient.

## Long documents

In the inspector, **Preview local narration parts (no generation)** plans up to 100 parts with the same compiler and 5,000-byte compiled limit used for the short synchronous path. It prefers whitespace boundaries, preserves Unicode boundaries and retains the complete source text. Confirm **Create these local documents** to add numbered documents to Library. The original is retained.

Each part still requires current connection, pricing and exact-request approval. Changing voice/language later requires its own preparation and size check. This is a local preparation aid, not automatic batch synthesis, retry, MP3 joining or a provider long-audio integration. If local creation fails midway, inspect Library for saved parts before creating another set; a completed Create button cannot be clicked twice in the same preview.

## What is saved

| State | Persistence |
|---|---|
| Documents, autosaves, checkpoints | Existing local database/content store |
| Google account metadata and credential binding | Existing database and Windows Credential Manager; no keys in preferences |
| Active pricing selection and activation history | Existing pricing history; independent of the pricing form |
| Current explicit pricing-source observation | A bounded, hash-only timeline record for each review; failure to record is displayed |
| Last voice/language and named voice setups | `studio-preferences.json` in the same app-data root; previous version retained |
| Theme and reduced-motion preference | Same non-secret preferences file |
| Provider access evidence | Existing persisted evidence, with its existing expiration; never granted by a saved setup |
| Exact-request spend approval | Single-use; never restored from preferences |
| Voice sample cache and accepted-byte Play/Export evidence | Session-only; old files are not silently trusted |

Unreadable or future-version preferences are preserved instead of overwritten. A failed preference write leaves the current session usable and reports that the choice could not be saved. Named setups contain voice/language intent only, not documents, credentials, price confirmation or spending authorization.

## Uncertain requests and cancellation

**Setups → Refresh local request status (offline)** reads persisted request counts and up to 100 rows. “No unresolved submission recorded” is not proof that audio completed. An unresolved row means the request may have been dispatched or charged. The display never resets that state.

**Stop waiting for generation** requests cancellation; it cannot promise cancellation of Google billing or work already dispatched. Inspect persisted status before attempting anything else. The current interface cannot automatically reconcile a lost synchronous response. Check the provider outcome/billing/support rather than clearing state or changing identities to force a duplicate.

## Data and recovery

The updated launcher keeps the existing app-data root. No database schema migration or credential reimport is required. Documents and preferences live in the workspace root; generated MP3s use `%LOCALAPPDATA%\CloudScribe Pro\Generated`, a separate folder. Back up exported audio as well as the closed workspace. Windows Credential Manager bindings do not transfer by copying ordinary files.

The original launcher and a closed-profile backup are retained for this local installation. Close the running app normally before switching launchers. The Settings restore action verifies an existing interrupted-restore journal; it is not a general backup wizard or a way to clear uncertain provider submissions.
