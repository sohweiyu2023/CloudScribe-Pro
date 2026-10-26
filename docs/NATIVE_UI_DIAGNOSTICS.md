# Local native UI diagnostics

The diagnostic extension records native application/resource initialization, window open/first-render/settled/activation/state/close, document-picker lifecycle, MP3 export lifecycle, and verified MP3 shell-open outcomes. It uses the existing best-effort local bootstrap sink. It does not add a network destination, telemetry, credentials, or permission access.

Each new entry has the bootstrap UTC timestamp and process ID, plus a diagnostic session ID, .NET runtime version and application module ID. Resource metadata includes application type, style count, shell-gradient color stops, window opacity, transparency capability and window state. Transparency capability alone does not mean that the visible shell is transparent: actual gradient alpha is the useful discriminator.

Actions accept only operation/phase enums and optional exception type/HResult. Filenames, paths, document text, transcript text, media bytes, status messages, credential values and exception messages are not accepted as diagnostic payloads. Import success is recorded by the view-model after completion, rather than inferring success merely because the picker returned a file. Playback reports `ShellOpened`, which is not proof of decoding or heard audio.

The bootstrap sink already caps files at 1 MiB, the directory at 8 MiB and retention at eight bootstrap files. It redacts sensitive patterns and contains recoverable write failures. The separate application JSON logger remains bounded by its existing configuration; this extension does not change those limits or disable existing checks. No screenshot capture is added to production.

This source variant has focused formatter/privacy tests and uses the repository's pinned SDK. Its native smoke test and any installation remain separate acceptance gates. Historical crash causality is not established by additional diagnostics. Never deliberately crash the app to test these records.
