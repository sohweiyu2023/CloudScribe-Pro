# CloudScribe Pro 1.0.1: setup and everyday use

The local usability update adds saved voice setups, remembered choices, offline request inspection and local narration-part preparation. See [Saved setups and local recovery](SAVED_SETUPS_AND_LOCAL_RECOVERY.md) for the updated workflow and persistence details.

CloudScribe turns written text into spoken MP3 audio using your Google Cloud Text-to-Speech account. Cosmic Transcriber does the reverse: it turns recordings into text. The two apps use separate providers, credentials and bills.

## Start on an already configured computer

Open the supplied `START-CLOUDSCRIBE.cmd` launcher. Keep the installation folder together: copying only the EXE will not work. The launcher can select the private .NET runtime and the correct local workspace.

Choose **Studio**. Press **Ctrl+N** to create a new document, give it a title, and type or paste a short passage. Open the workspace inspector to see the five voice-workflow sections. Close the inspector drawer when you need to edit the document on a narrow window.

If Google was configured previously, do not create another account or paste the key again. In **2 - Choose a voice**, set the language, for example `en-US`, optionally search `Standard`, then select **Refresh real Google voices**. This renews the saved connection's authenticated evidence and loads current voices. Select the exact voice you want.

## Set up Google Cloud once

You need your own Google Cloud account, an enabled billing account, a project with Cloud Text-to-Speech enabled, and a service-account JSON key. Google charges your Cloud account; a ChatGPT subscription does not pay these charges.

1. In Google Cloud Console, select or create a dedicated project and link the billing account you intend to pay from. Verify both identities before proceeding.
2. Enable **Cloud Text-to-Speech API** for that project.
3. Create a service account for CloudScribe. The tested configuration used the project's **Service Usage Consumer** role. Do not add Owner or Editor merely to get past an error. Organization policies may restrict keys; follow your administrator's supported credential policy.
4. Create/download the service account's JSON key yourself. Treat this file like a password. Do not put it into Git, email, screenshots or support reports.
5. In CloudScribe's **1 - Connect Google**, enter new local identifiers such as `google-home` for Account ID and `google-home-key` for Credential name, plus a display name and region `global`. Local identifiers use lowercase letters, numbers, periods or hyphens.
6. Use `https://texttospeech.googleapis.com/v1/voices` for the voice catalog and `https://texttospeech.googleapis.com/v1/text:synthesize` for synthesis. Both endpoints must share the admitted Google API origin.
7. Paste the JSON directly into **Service-account JSON**, then choose **Configure Google TTS securely**. CloudScribe stores the credential in Windows Credential Manager. The field clears on both success and failure: check the status message and successful voice refresh to confirm setup.

Configuration validates real catalog access and sends an input-free capability probe; it does not generate an audio sample. Later requests obtain short-lived OAuth tokens through the saved binding. Do not repeat fresh configuration for an existing account: it deliberately refuses to overwrite a credential binding.

Official references: [Google Text-to-Speech setup](https://cloud.google.com/text-to-speech/docs/before-you-begin), [service-account key management](https://cloud.google.com/iam/docs/creating-managing-service-account-keys), and [pricing](https://cloud.google.com/text-to-speech/pricing).

## Review pricing and generate

1. In **3 - Confirm current pricing**, review Google's current pricing page and compare the relevant rates with the included catalog. Tick the match confirmation only if they agree, then choose **Fetch current pricing evidence and activate**. A successful fetch alone does not prove that prices match.
2. In **4 - Review & approve**, choose **Prepare exact request and show price**. Check the selected voice, currency, subtotal and compiled payload identity.
3. Enter a **Spending limit - minor units** at least as large as the displayed amount. For USD, `1` means one cent and `100` means one dollar. This is a per-request application check, not a Google billing-account cap.
4. Tick **I approve this request and spending limit**, then choose **Approve displayed spend**.
5. Choose **Generate with Google** once. The text is sent to Google at this point. Wait for a terminal status before doing anything again.
6. In **5 - Listen & export**, use **Play verified MP3** to open the accepted file in your default player. Use **Export verified MP3** and select your desired folder/filename in the Windows Save dialog. Export preserves the accepted MP3 bytes; it does not generate or bill again.

The displayed subtotal is conservatively rounded from the admitted provider character rate and the exact compiled input, using a UTF-8 byte upper bound. It ignores free allowances and credits, and excludes tax and currency conversion. Your billing account may use a different currency. Review actual Google billing separately.

Approval is single-use. A new prepared request needs fresh confirmation. Editing text or changing the voice requires a new review. The current synchronous workflow accepts short documents within a conservative 5,000-byte compiled-envelope ceiling; split longer passages into shorter documents. Unknown or unpriced voice families are rejected rather than assigned a guessed rate.

## Save, reopen and troubleshoot

Documents and connection metadata are local. Open saved documents through **Library**; startup may show the welcome screen. Generated MP3s are saved under `%LOCALAPPDATA%\CloudScribe Pro\Generated`. Export files you want to keep in your own audio folder.

The current session's Play/Export controls retain accepted-byte integrity evidence in memory. After restarting the app, existing MP3 files remain on disk, but the last-output controls are not restored automatically. Open an already saved/exported MP3 directly in your player; do not regenerate simply to listen again.

| Symptom | What to do |
|---|---|
| Voices or capability evidence expired | Choose **Refresh real Google voices** to revalidate the saved connection. Evidence expires after about 30 minutes. |
| No voices | Check language/search filters, saved account access, API enablement and billing. Refresh, then choose an actual returned voice. |
| Pricing cannot be activated | Read the displayed error. Verify the official HTTPS page and rate match. Do not confirm mismatching rates. |
| Generate is disabled | Prepare, enter a sufficient limit, tick consent and approve the current request. |
| Request stopped or reconciliation required | Inspect **Queue** before retrying. A lost response can still have incurred a charge. Do not repeatedly click Generate. |
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
