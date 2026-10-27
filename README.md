# CloudScribe Pro

CloudScribe Pro 1.0.1 is a Windows text-to-speech workspace for Google Cloud voices. Write or import a document, review the exact request and its estimated maximum cost, approve it, then create, play and export a verified MP3.

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
- A guided setup walkthrough, voice comparison and separately approved short samples.
- Local narration-part preparation for longer text. Each part still requires its own review and approval.

The current generation path is for short requests within a conservative **5,000-byte compiled-envelope limit**, including JSON escaping and request fields. It sends plain text; automatic long-audio batches, multi-speaker script production and custom SSML lexicons are not supplied by this interface. Prices require current admitted evidence and user source review; the app does not promise live billing-account tracking. See [saved setups and recovery](docs/SAVED_SETUPS_AND_LOCAL_RECOVERY.md) and [voice comparison](docs/VOICE_COMPARISON.md).

## Your data and provider access

Documents and preferences are stored locally. Google access checks contact Google; approved synthesis sends the prepared request text to its configured Google endpoint. Credentials use Windows Credential Manager. Never share your profile, database, documents, recordings or service-account key with the application package. Each recipient needs their own provider access. See [clean sharing](docs/SHARING_WINDOWS.md).

Cancellation or a lost response does not prove a dispatched request was uncharged. Inspect persisted request status before retrying. Existing saved MP3s remain on disk; trusted in-app Play/Export state is session-only.

## Build and validation

Building requires the exact SDK in `global.json`, currently **.NET SDK 10.0.402**. See [Windows build and validation](BUILDING-WINDOWS.txt), [installation and updates](docs/WINDOWS_INSTALLATION.md), and [verified release uploads](docs/R2_RELEASE_UPLOADS.md).

As of 21 October 2026, source CI passes 718 compiled mocked/local tests plus strict Release build and formatting. These are separate from the historical 19 October live check: two short Google Standard MP3 requests passed decoding/export checks, with the first user-confirmed audible; that repair had 662 compiled tests. Neither result is a claim that every voice or long-document workflow has been live-tested. Historical stage certification details remain in the development records.

## License

Original CloudScribe Pro project code is available under the [MIT License](LICENSE), copyright (c) 2026 Soh Wei Yu. Third-party packages, native components and incorporated upstream material retain their own licenses and notices; see [Third-party notices](THIRD-PARTY-NOTICES.md). The MIT grant covers only rights the project copyright holder can grant. It does not change provider-service terms or license the separately distributed Cosmic Transcriber project/reference archive. Existing About provenance and credits are retained.

See [voice recommendations and Chirp controls](docs/VOICE_RECOMMENDATIONS.md) for exact-language defaults, premium pricing and preserved manual choices.
