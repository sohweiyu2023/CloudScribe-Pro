# CloudScribe Pro 1.0.1: setup and everyday use

The local usability update adds saved voice setups, remembered choices, offline request inspection and local narration-part preparation. See [Saved setups and local recovery](SAVED_SETUPS_AND_LOCAL_RECOVERY.md) for the updated workflow and persistence details.

CloudScribe turns written text into spoken MP3 audio using your Google Cloud Text-to-Speech account. Cosmic Transcriber does the reverse: it turns recordings into text. The two apps use separate providers, credentials and bills.

## Help inside the application

**Help** opens a live walkthrough that points to actual controls, opens the relevant screens and scrolls targets into view. It has 15 steps for first-time setup and 11 for a saved connection, plus labeled Google setup illustrations. **Back**, **Next**, **Skip guide**, **Finish guide** and **Show control** keep navigation simple; Escape skips, and Help replays it. Guide navigation never approves costs or generates speech. See [Visual walkthrough](VISUAL_WALKTHROUGH.md). **About** shows the maintainer, website and optional support email.

To give a copy to a friend, use a clean app/runtime package. Never include your personal profile, documents, MP3s, service-account JSON, Windows credentials or billing access. Your friend must connect their own Google account. See [Safe Windows sharing](SHARING_WINDOWS.md).

## Start on an already configured computer

Open the supplied `START-CLOUDSCRIBE.cmd` launcher. Keep the installation folder together: copying only the EXE will not work. The launcher can select the private .NET runtime and the correct local workspace.

Choose **Studio**. Press **Ctrl+N** to create a new document, give it a title, and type or paste a short passage. Choose the prominent **Create MP3** button. Its cost, approval and creation controls remain visible while voice choices scroll above. Close an inspector drawer when it blocks the toolbar on a narrow window.

If Google was configured previously, do not create another account or paste the key again. In **Create MP3**, choose a saved setup if desired. Open **Language and voice filters** only when you need to change the remembered language or search, then select **Refresh real Google voices**. This renews the saved connection's authenticated evidence and loads current voices. Select the exact voice you want.

## Set up Google Cloud once

You need your own Google Cloud account, an enabled billing account, a project with Cloud Text-to-Speech enabled, and a service-account JSON key. Google charges your Cloud account; a ChatGPT subscription does not pay these charges.

1. In Google Cloud Console, select or create a dedicated project and link the billing account you intend to pay from. Verify both identities before proceeding.
2. Enable **Cloud Text-to-Speech API** for that project.
3. Create a service account for CloudScribe. The tested configuration used the project's **Service Usage Consumer** role. Do not add Owner or Editor merely to get past an error. Organization policies may restrict keys; follow your administrator's supported credential policy.
4. Create/download the service account's JSON key yourself. Treat this file like a password. Do not put it into Git, email, screenshots or support reports.
5. In Studio's **Advanced tools and connection settings → Connect or change Google account**, enter new local identifiers such as `google-home` for Account ID and `google-home-key` for Credential name, plus a display name and region `global`. Local identifiers use lowercase letters, numbers, periods or hyphens.
6. Use `https://texttospeech.googleapis.com/v1/voices` for the voice catalog and `https://texttospeech.googleapis.com/v1/text:synthesize` for synthesis. Both endpoints must share the admitted Google API origin.
7. Paste the JSON directly into **Service-account JSON**, then choose **Configure Google TTS securely**. CloudScribe stores the credential in Windows Credential Manager. The field clears on both success and failure: check the status message and successful voice refresh to confirm setup.

Configuration validates real catalog access and sends an input-free capability probe; it does not generate an audio sample. Later requests obtain short-lived OAuth tokens through the saved binding. Do not repeat fresh configuration for an existing account: it deliberately refuses to overwrite a credential binding.

Official references: [Google Text-to-Speech setup](https://cloud.google.com/text-to-speech/docs/before-you-begin), [service-account key management](https://cloud.google.com/iam/docs/creating-managing-service-account-keys), and [pricing](https://cloud.google.com/text-to-speech/pricing).

## Review pricing and create an MP3

1. Choose **Create MP3** in the toolbar. Check the current document and remembered voice. Selecting a named setup restores its voice/language preference; **Refresh real Google voices** validates it using the saved connection. Choose an actual returned voice.
2. The active pricing status is shown. If pricing is unavailable or needs review, expand **Review pricing source if pricing is unavailable**, compare the official rates with the included catalog, confirm the match yourself, and activate the observed source. Fetching a page does not verify its rates automatically.
3. Choose **Review current text, voice and cost**. This compiles the exact current request and shows a conservative list estimate without generating audio.
4. Inspect the displayed voice, currency and amount. Tick **I approve this exact request and its displayed maximum cost**, then choose **Create MP3 — approve and send to Google** once. The application ceiling is the displayed amount in the estimate's currency; this is not a Google billing-account cap.
5. Wait for the result. **Listen, save and find audio** contains **Play verified MP3**, **Export verified MP3** and folder access. Play/export preserve accepted bytes and do not synthesize again. **Stop waiting** appears during generation; stopping a dispatched request does not prove it was uncharged.

Opening **Create MP3** automatically checks the saved Google connection and loads verified voices. A remembered voice is selected only after it appears in the verified list. Choosing a saved setup reloads and validates its voice too. Loading can take up to 30 seconds; closing the window cancels the wait. This checks access without generating speech. A fresh profile gets connection instructions instead of an unexplained empty list.

Beside the voice selector, **Hear voice samples - Google (browser)** opens Google's public table of prerecorded examples. This works before connecting and sends no document or CloudScribe key. **Hear selected voice sample - Google (browser)** opens an exact prerecorded example only for an allowlisted voice whose link was observed in that official table; otherwise it stays disabled with an explanation. The initial checked links cover 69 English (US) voices, observed on 20 October 2026. **Copy selected voice name** helps find other voices: open the table, press Ctrl+F, paste the name and use the audio player in its row if available. Public examples do not read your document or change your selected voice. Links may change; use the table if an old direct link stops working. The separate in-app **Generate sample** flow uses your Google account and may be billed after review and approval.

The visible next-step message explains why review or creation is unavailable and what to do next. Long text gets directions to **Advanced tools → Preview local narration parts (no generation)**; the short compiled request must fit 5,000 bytes including request fields and escaping. Local parts are text documents, and every part still needs its own review and explicit approval. **Listen, save and find audio** starts expanded, explains session-only playback/export readiness and provides the saved MP3 folder for older recordings. Opening a saved MP3 in your player needs no new synthesis.

The voice area shows loading, ready, no matching voices, or a connection error. **Language and voice filters** contains the filters; the applied language/search summary stays visible. **Clear voice search and reload** removes the name search while keeping your chosen language. A selected verified voice is named below the list. **Review current text, voice and cost** requires text and a verified voice; **Create MP3** becomes available after a current cost review and explicit approval. Connection setup is hidden when exactly one enabled Google connection is saved; **Change connection** deliberately reveals the fresh-configuration form inside Advanced tools. It never reads your key back into the form or overwrites the old credential binding.

The detailed separate prepare/ceiling/approve/generate controls remain in **Advanced tools → Detailed request review** for inspection. The guided path uses the same production approval, capability, pricing, queue and byte-integrity checks.
The displayed subtotal is conservatively rounded from the admitted provider character rate and the exact compiled input, using a UTF-8 byte upper bound. It ignores free allowances and credits, and excludes tax and currency conversion. Your billing account may use a different currency. Review actual Google billing separately.

Approval is single-use. A new prepared request needs fresh confirmation. Editing text or changing the voice requires a new review. The current synchronous workflow accepts short documents within a conservative 5,000-byte compiled-envelope ceiling; split longer passages into shorter documents. Unknown or unpriced voice families are rejected rather than assigned a guessed rate.

## Save, reopen and troubleshoot

Documents and connection metadata are local. Open saved documents through **Library**; startup reopens the remembered document when it is in the current recent local-library list, otherwise it uses the latest available document. An empty library shows the welcome screen. Generated MP3s are saved under `%LOCALAPPDATA%\CloudScribe Pro\Generated`. Export files you want to keep in your own audio folder.

The current session's Play/Export controls retain accepted-byte integrity evidence in memory. After restarting the app, existing MP3 files remain on disk, but the last-output controls are not restored automatically. Open an already saved/exported MP3 directly in your player; do not regenerate simply to listen again.

| Symptom | What to do |
|---|---|
| Voices or capability evidence expired | Choose **Refresh real Google voices** to revalidate the saved connection. Evidence expires after about 30 minutes. |
| No voices | Read the loading/error message. Expand **Language and voice filters** and check the language (for example en-US); use **Clear voice search and reload**. For an access error, inspect Setups and Google API/billing permissions. Do not replace a saved key just to retry. |
| Pricing cannot be activated | Read the displayed error. Verify the official HTTPS page and rate match. Do not confirm mismatching rates. |
| Review or Create MP3 is disabled | Add text, wait for voices to load, select a verified voice, review the current cost, then tick consent. Editing the request invalidates the old review. |
| Request stopped or reconciliation required | Inspect persisted request status in **Setups** before retrying. A lost response can still have incurred a charge. Do not repeatedly click Generate. |
| Nothing plays | Confirm the MP3 exists and your default audio player works. Export/listen locally without a new synthesis. |

Back up documents and exported audio before moving/removing an installation. Windows Credential Manager entries are separate from ordinary app files and do not transfer by copying the installation directory. Keep credentials out of backups you share.

## Build from source on Windows

Install the exact SDK selected by `global.json` (currently **.NET SDK 10.0.401**). From a fresh source folder:

```powershell
dotnet restore CloudScribe.sln --locked-mode --configfile NuGet.config
dotnet build CloudScribe.sln -c Release --no-restore
dotnet test CloudScribe.sln -c Release --no-build --no-restore
dotnet publish src/CloudScribe.App/CloudScribe.App.csproj -c Release --no-build --no-restore -o ..\CloudScribe-Windows
```

Run `CloudScribe-Windows\CloudScribe.exe` with the compatible .NET runtime available. A supplied installation launcher may instead use a private runtime and workspace override. Keep your existing working installation/data separate while testing a new build.

## Verified acceptance

On 19 October 2026, two distinct short `en-US-Standard-C` documents passed real Google synthesis, MP3 decoding and byte-identical app export. The first was audibly confirmed by the user in VLC. The local repair passed 662 compiled tests and the native interaction/visual checks. These results cover this short synchronous MP3 path; they do not certify every voice, language, long-document workflow or future provider pricing.
