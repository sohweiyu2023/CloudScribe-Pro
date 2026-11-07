# Changelog

All notable changes to CloudScribe Pro. Dates are Singapore time. Costs shown in the app are estimates and safety holds, never a Google bill.

## 1.0.2 (7 November 2026)

### Long Audio and article MP3
- **More controllable network waits.** TCP keepalive helps detect unresponsive peers; timing depends on the operating system and network and does not detect every hung live connection. A status check with no answer after two minutes is replaced by the next check; nothing is resubmitted. One private download is bounded to 30 minutes and resumes with GETs only. Synthesis POSTs have no application deadline; cancellation or transport failure after dispatch remains an uncertain outcome, never permission to resend.
- **Stop waiting now** (usable again on every later run) ends a wait immediately after **Stop after current action** has been requested. A request Google already received may still finish and be charged; its saved outcome is checked before anything else is sent.
- **Reduced clipping risk:** newly assembled MP3s use fixed 1.5 dB attenuation before encoding; this is not a guarantee for every input. (Google's parts peak at full scale; previously a 74-minute article decoded at +0.35 dBFS with 620 over-range samples.)
- Newly assembled MP3s carry the article title as its ID3 title; source WAV tags are not copied.
- After a new MP3 is verified, cleanup attempts to remove its numbered duplicate WAV snapshots. Original verified WAVs and existing completed outputs are kept; inaccessible copies remain.
- Remembers your approved FFmpeg/ffprobe pair per profile, rechecks both files before every use and, when it must ask again, says why (changed, missing or unreadable setting). Chocolatey shims are refused with guidance to the real binary folder.
- Run summary: time since the job started (with pauses), active working time, number of reserved request attempts (not a verified dispatch count), the list-price estimate for the article, and safety holds — clearly separated, none presented as a bill. Logged as event 1622 when a run ends.
- Plain-language Long Audio window: stage headline, five-step tracker (Prepare · Create speech · Join · Check · Done), coverage bar, readable file size, Play / Save a copy / Open folder, and a title-based suggested file name.
- Sections Google rejects for over-long sentences are split only at existing punctuation with source text and order preserved, then joined from verified WAVs and encoded to MP3; deferred-unknown and explicitly approved replacement ranges are supported (administrative setup).

### Look and feel
- All dialogs and the guided walkthrough use the main window's violet and gold theme. Create MP3 has a proper heading and a fixed action bar separated from the scrolling content.

### Logging
- New warning events: 1625 (status check unanswered within its deadline) and 1626 (user stopped waiting). Summary events 1622–1624 cover timing and holds. Logs contain no article text, credentials or output paths.

### Documentation and release hygiene
- New uninstall guide (app, data, Credential Manager entry and Google key revocation).
- README corrected: synchronous 5,000-byte limit, the full-article and Long Audio paths, current test counts.
- Source line endings normalised to LF; formatter clean; `SHA256SUMS.txt` regenerated.
- `COM0` and `LPT0` are treated as reserved Windows file names.

### Known limitations
- Recovering from an unanswered (unknown) request, or raising the spending cap, still needs the administrative scripts in `docs/LONG_AUDIO_BUDGET_RECOVERY.md`.
- Each split request reserves at least 1 US cent, so holds can far exceed the list-price estimate for heavily split articles.
- Long Audio runs in a window that stays open until the run stops.

## 1.0.1 (servicing updates, October – 6 November 2026)

Highlights of the most recent 1.0.1 updates; see the Git history for the full list.

- Long Audio–first article recovery with verified output (PR #117).
- Chinese article narration with durable split recovery and diagnostics (PR #116).
- Installer recovery for interrupted diagnostic installs and pending installations (PRs #112–#113).
- Windows dependency refresh to .NET SDK 10.0.402.

## 1.0.0

- First certified release: local documents and library, Google voice discovery, exact-request cost review and single-use approval, verified MP3 playback and export, guided walkthrough.
