# Saved setups, autosave and local recovery

Saved setups retain local preferences while the existing Google credential boundary and explicit spending approval remain authoritative. For the current guided Create MP3 flow, see [the user guide](USER_GUIDE.md).

## Everyday use

1. Open the installed **CloudScribe Pro** shortcut or the portable package's `START-CLOUDSCRIBE.cmd`. Reopen a document in **Library**, or use **Ctrl+N** / **Ctrl+O** to create/import one. Text and title autosave after about 750 ms; **Ctrl+S** creates a checkpoint. Wait for the saved state before ending a session.
2. If Google is already connected, open **Create MP3** to reuse and check it. Under **Language and voice filters**, set the language and use **Refresh real Google voices** when needed. Access/capability evidence must still be refreshed when stale.
3. Select a returned voice. Its voice/language choice is remembered automatically. To name it, close an inspector drawer if necessary, choose **Setups**, enter a name and choose **Save current voice setup**.
4. In a later session, **Setups > Load selected setup** restores only intent. Close the dialog and refresh voices, or choose the saved setup inside **Create MP3**, which reloads and validates its voice. The exact matching returned voice is selected; a missing preferred voice is not silently replaced with a different one. Saved setups retain voice/language intent; voice search is a session filter and does not authorize provider access.
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

App updates preserve the existing profile selection; see [Windows installation and updates](WINDOWS_INSTALLATION.md). Documents and preferences live in the app-data profile; generated MP3s use `%LOCALAPPDATA%\CloudScribe Pro\Generated`, a separate folder. Back up exported audio as well as the closed profile. Windows Credential Manager bindings do not transfer by copying ordinary files.

Close the running app normally before updating or backing up its profile. Retain your own closed-profile backup and rollback package. The Settings restore action verifies an existing interrupted-restore journal; it is not a general backup wizard or a way to clear uncertain provider submissions.


## Separately approved successor attempts

An exhausted grant remains exhausted. A new attempt after a completed HTTP 400 invalid-argument rejection needs a new independently reviewed installer-admitted schema3 policy and a fresh explicit review. The policy binds the unchanged saved document/revision, request bytes, account/project, installed modules/profile, exact latest consumed child and earlier grant, current financial pins, source approval and short expiry. It permits one new attempt with at most three additional USD cents and carries no historical audio-import authority. Pending, unknown, accepted and unresolved attempts cannot use this path. No automatic retry is created.

If separately approved, an allocation increase binds the exact previous scope and approved old/new allocation ceiling. The project cap and retained commitments cannot be reduced or reset. Reservation, scope transition and append-only grant audit are committed together; interruptions roll back all of them. Reopening a committed reservation resumes only its existing identity, with no duplicate charge reservation. The same source approval cannot be rotated into another successor grant for that narration. Expired or changed authority prevents dispatch while preserving all earlier holds. This mechanism does not settle billing or explain a provider rejection.
