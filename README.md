# CloudScribe Pro

CloudScribe Pro is a Windows text-to-speech workspace for Google Cloud voices. Write or import a document, review the exact request and its estimated maximum cost, approve it, then create, play and export a verified MP3.

This source contains **CloudScribe Pro 1.0.5**. See the [changelog](CHANGELOG.md) for the current features and [Windows installation guide](docs/WINDOWS_INSTALLATION.md) for preserving an existing setup.

**[Download for Windows x64](https://files.awakeningtoreality.com/CloudScribe-Windows-x64.zip)** · **[Setup and user guide](docs/USER_GUIDE.md)** · **[Source code](https://github.com/sohweiyu2023/CloudScribe-Pro)**

The ZIP contains the runnable application and a private .NET runtime. GitHub's source-code ZIP is for building the application, not the Windows download.

## Quick start

1. Extract the whole Windows ZIP and run `START-CLOUDSCRIBE.cmd`. For a permanent Start Menu shortcut, follow [Windows installation](docs/WINDOWS_INSTALLATION.md). Keep the app and runtime folders together.
2. Bring your own Google Cloud project, billing account and service-account JSON key. Follow the in-app Help guide or [Google setup instructions](docs/USER_GUIDE.md#set-up-google-cloud-once). An existing saved connection can be reused.
3. Open or create a document, choose **Create MP3**, and wait for the saved connection's voice list to load. Select a verified voice.
4. Review the current text, voice and cost, approve that exact request, then create once. Use the accepted output's Play/Export controls, or listen to older saved MP3 files with your usual player.

Google bills your own Cloud account. A ChatGPT subscription does not include Google synthesis. Voice samples generated inside the app also require their own cost review and approval; Google's prerecorded browser samples are separate.

## What it supports

- Local documents, autosave/checkpoints, a recent library, and TXT/Markdown/HTML/DOCX or clipboard import.
- Authenticated Google voice discovery, remembered voice/language choices and named setups.
- Exact-request preparation, explicit single-use cost approval, and verified MP3 playback/export.
- A guided setup walkthrough, voice comparison and separately approved short samples. For longer Neural2 or Chirp 3 HD comparisons, save the same text as a separate document for each voice and choose **Review full document with this voice** in **Voices**. Each document needs its own exact admission and approval.
- Local narration-part preparation for longer text. Each part still requires its own review and approval.
- Open **Batch MP3** beside **Create MP3**, or use the **Queue** drop area, to [narrate TXT files](docs/BATCH_NARRATION.md), with one MP3 per file, an installed batch profile and exact batch approval. Existing engine admissions and spending caps still apply.
- Skip web addresses in audio using a previewed, saved narration copy; the original text stays in Library. Available in **Create MP3** and **Batch MP3**, before the copy's own cost review and approval.
- [One continuous article MP3](docs/ARTICLE_MP3_EXPORT.md) from complete, verified local part outputs, using an existing FFmpeg installation. Missing or uncertain parts block export; this action never synthesizes audio.

A single **Create MP3** request is limited to Google's synchronous **5,000-byte** input (the app counts JSON escaping and request fields too). Longer text uses **Review full article and cost**, which narrates an admitted article in parts after one cost review and joins them into one MP3. Neural2 packs plain text against the exact serialized request-byte ceiling; existing Chirp planners retain their own boundaries and plan identities. The full-document path does not impose the short-sample box's 500-character limit or a new arbitrary short-document limit. Existing local planning bounds of 1,000,000 Unicode scalars and 10,000 coverage parts, per-request byte checks, admitted budgets and provider restrictions still apply.

Full-article generation requires a separately reviewed article policy; the clean package does not authorize arbitrary articles. An optional, separately provisioned [Google Long Audio](docs/GOOGLE_LONG_AUDIO.md) path sends a whole saved article as one asynchronous job; if Google reports over-long sentences, its admitted adaptive mode splits only that section at existing punctuation and assembles one MP3. Multi-speaker scripts and custom SSML lexicons are not supplied. Prices require current admitted evidence and user source review; the app does not promise live billing-account tracking. See [saved setups and recovery](docs/SAVED_SETUPS_AND_LOCAL_RECOVERY.md) and [voice comparison](docs/VOICE_COMPARISON.md).

## Your data and provider access

Documents and preferences are stored locally. Google access checks contact Google; approved synthesis sends the prepared request text to its configured Google endpoint. Credentials use Windows Credential Manager. Never share your profile, database, documents, recordings or service-account key with the application package. Each recipient needs their own provider access. See [clean sharing](docs/SHARING_WINDOWS.md).

Cancellation or a lost response does not prove a dispatched request was uncharged. Inspect persisted request status before retrying. Existing saved MP3s remain on disk; trusted in-app Play/Export state is session-only.

## What changed

See the [changelog](CHANGELOG.md).

## Uninstall

See [Uninstall and remove your data](docs/USER_GUIDE.md#uninstall-and-remove-your-data). Removing the app does not delete your documents, audio or Google key unless you choose to.

## Build and validation

For prerequisites, a first checkout, quick checks, the full test suite, and CI-equivalent offline safety checks, start with the [developer guide](docs/DEVELOPMENT.md).

Building requires the exact SDK in `global.json`, currently **.NET SDK 10.0.402**. See [Windows build and validation](BUILDING-WINDOWS.txt), [installation and updates](docs/WINDOWS_INSTALLATION.md), and [verified release uploads](docs/R2_RELEASE_UPLOADS.md).

The source has 1,300+ compiled mocked/local test methods (2,100+ test cases including theories) plus strict Release build and formatting gates; check results on the exact commit you build. Earlier, as of 21 October 2026, CI passed 718 tests. These are separate from the historical 19 October live check: two short Google Standard MP3 requests passed decoding/export checks, with the first user-confirmed audible; that repair had 662 compiled tests. Neither result is a claim that every voice or long-document workflow has been live-tested. Historical stage certification details remain in the development records.

## License

Original CloudScribe Pro project code is available under the [MIT License](LICENSE), copyright (c) 2026 Soh Wei Yu. Third-party packages, native components and incorporated upstream material retain their own licenses and notices; see [Third-party notices](THIRD-PARTY-NOTICES.md). The MIT grant covers only rights the project copyright holder can grant. It does not change provider-service terms or license the separately distributed Cosmic Transcriber project/reference archive. Existing About provenance and credits are retained.

See [voice recommendations and Chirp controls](docs/VOICE_RECOMMENDATIONS.md) for exact-language defaults, premium pricing and preserved manual choices.

## Private validation diagnostics

Optional [private provider diagnostics](docs/PRIVATE_PROVIDER_DIAGNOSTICS.md) can retain unfamiliar validation messages locally after redaction. Capture is off by default, applies to one exact saved request, and never grants spending or retry permission.
