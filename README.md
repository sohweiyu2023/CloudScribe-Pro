# CloudScribe Pro

CloudScribe Pro 1.0.1 is a Windows text-to-speech workspace with local documents, Google Cloud voice discovery, explicit request pricing approval, and verified MP3 playback/export.

Start with the [setup and user guide](docs/USER_GUIDE.md). It covers an existing installation, first-time Google setup, voice selection, pricing, generation, exports and recovery. The source SDK is pinned by `global.json` to .NET 10.0.401; the guide includes build commands.

For development, see [Windows build and validation](BUILDING-WINDOWS.txt). For distribution, see [clean sharing](docs/SHARING_WINDOWS.md), [installation and updates](docs/WINDOWS_INSTALLATION.md), and [verified release uploads](docs/R2_RELEASE_UPLOADS.md).

The tested repair connects Studio to the existing Stage6 admission and single-use spend gates, renews saved-connection evidence using real authenticated observations, and corrects credential-target and current-pricing onboarding failures. It preserves queue reconciliation and refuses unknown prices or unresolved submissions.

Two short live Google Standard MP3 samples passed on 19 October 2026, including full decode and byte-identical app export; the first was user-confirmed audible. The repair also passed 662 compiled tests. Live testing is distinct from the broader synthetic/native checks and does not certify every model or workflow.

Google usage is billed to your Google Cloud account. Never commit service-account keys, local databases, user documents or generated private audio. Historical Stage8 governance evidence retains its original 1.0.0 identity; the application is 1.0.1.

## License

Original CloudScribe Pro project code is available under the [MIT License](LICENSE), copyright (c) 2026 Soh Wei Yu. Third-party packages, native components and incorporated upstream material retain their own licenses and notices; see [Third-party notices](THIRD-PARTY-NOTICES.md). The MIT grant covers only rights the project copyright holder can grant. It does not change provider-service terms or license the separately distributed Cosmic Transcriber project/reference archive. Existing About provenance and credits are retained.
