# Changelog

All notable changes to CloudScribe Pro. Dates are Singapore time. Costs shown in the app are estimates and safety holds, never a Google bill.

## 1.0.4 (8 November 2026)

Batch window usability and safety follow-up. No change to spending rules, caps, approvals or provider requests.

### Windows review fixes
- Draft Undo now preserves the previous arrangement instead of retaining the mutable current list.
- Saving rereads local TXT files even when text is cached; changed files use their current text and missing files block saving. Resuming a saved import always checks its pinned source hash.
- A file that failed before import can be removed and added again, while saved audit history and started-file protection remain intact.
- Switching between batches with identical visible rows refreshes their identities, so selection and row actions target the current batch.

### Adding and arranging files
- **Drag and drop** TXT files and whole folders from File Explorer into the batch list, with a highlighted drop area and plain messages for anything left out. **Add a folder…** accepts several folders and can **include subfolders**. The pickers stay.
- New files are appended without disturbing an arranged order, sorted naturally by folder and name. The same TXT added twice is ignored and reported; one TXT always makes one MP3, even when two files have identical text.
- **Drag rows to reorder**, with a gold insertion line and auto-scroll near the list edges. **Multi-select** (Shift/Ctrl) moves files together in their relative order. Keyboard: Alt+Up/Down, Alt+Home/End, Delete, Ctrl+Z, Enter to play. **Undo** covers moves, removals, adds and discarding a draft.
- **Fixed:** "same text as file NN" notes used the row number from when a file was added, so they pointed at the wrong file after a move or removal. They now follow the current order and name the earlier file.
- **Fixed:** track-number width counted excluded files (100 files with 5 unusable could give three-digit numbers).

### Drafts, voices and saved batches
- **Unsaved drafts are kept automatically** (`batches\draft.json`: names, paths, sizes and hashes, never text). Closing the window, switching to a saved batch, a missing voice or a crash no longer loses the list. **Discard draft** is undoable while the window is open.
- **Fixed the missing-voice dead end:** the voice is chosen inside the batch window (with **Refresh voices**), and the draft waits if Google is not connected yet. Saved batches offer **Use this batch's voice**.
- Saving reads every file again: a missing file is named and nothing is saved; a file changed since listing is taken as it is now and reported.
- **Saved, not-yet-started batches are editable:** add files, move or remove waiting files, and change the output folder until the first file starts. Additions need a new review and approval; moves and removals keep the approval.
- **Interrupted or cancelled adds resume** when the batch is reopened, by reading the files again with their SHA-256 checked. A changed file is refused instead of being narrated with different text. Files are never added to the library twice.

### Guidance, rehearsal and results
- A **step guide** (Files → Voice and output → Review → Run → Results) with a **Next:** line, and tooltips on unavailable buttons that list every missing prerequisite. When this computer has no batch spending setup, the window says a paid batch cannot start; it no longer implies one is ready.
- **Fixed:** the rehearsal could report a voice or output-folder problem and still end "All N remaining file(s) are ready". Its verdict now counts every blocking check (voice, folder, spending setup, unfinished adds, approval and each file), lists failed and needs-checking files separately, and appears under Review and approval.
- Money is shown in three labelled parts: the estimate (upper limit), the approved allowance, and holds plus any file whose request may have reached Google. **A possible earlier charge stays visible** after replacement audio is verified (new `UncertainOutcomes` counter on each file).
- **Results** keep done, failed, needs checking, skipped and waiting apart. **Open output folder** and **Play selected MP3** were added.

### Responsiveness and accessibility
- Reading, checking and adding files show progress and can be **cancelled**; work finished before the cancel is kept. Repeated or overlapping clicks are refused with a message instead of running twice. Long queues use a virtualised list; there is no file-count limit.
- Selection and keyboard focus stay on the moved files; status and next-step lines are announced politely to screen readers; button rows wrap in small windows.
- **Fixed:** changing the engine, failure policy or track numbering in a draft could be reset by the window's own refresh before it was applied.
- New redacted diagnostic stages (draft save/load/discard, drop, drag reorder, undo, voice choice, open output, cancel) use the existing batch action event.

## 1.0.3 (8 November 2026)

### Batch MP3 from TXT files (new)
- **Create MP3 → Batch MP3 from TXT files…** turns several TXT files, or a folder, into one MP3 each. One voice, one review and one approval cover the batch. Source files are only read; each becomes its own library document, exactly like Import. Files are never merged, and no extra tracks are created.
- **Long Audio first.** Each file is narrated by Google Long Audio when the installed Long Audio scope can take it (same voice and language, up to 1 MB, fits the cap). The whole file goes as one request and is split only after a confirmed sentence-length failure (existing adaptive recovery), giving one verified MP3. Other files use the ordinary engine. Each file's engine and maximum are shown before approval; an engine is never switched after a file starts.
- **One approval for the batch.**
  - In-app (one click) or reviewed (one exported request, installed once with `scripts/Install-BatchAuthority.ps1`).
  - For Long Audio files, the approval admits each exact request within the installed Long Audio scope's voice, bucket, pricing and total cap, and its ledger. Installed jobs are never affected.
  - Ordinary-engine files use the service's exact review when they are already admitted, and say clearly when they still need an article admission.
  - Default limits of about S$25 per batch and S$100 per 30 days cap what an approval may cover; they are never an approval themselves.
- **Order and names.**
  - Natural order on first add; files added later keep the arranged order.
  - Identical text is kept and only noted.
  - Optional track numbers are never added twice.
  - Clashing names are reported and resolved with `(n)`; nothing is ever overwritten.
- **Reliability.**
  - The queue survives restarts and recovers from a damaged queue file, which is kept aside.
  - Duplicate starts are refused.
  - The job is recorded before any paid request; unknown outcomes are never resent (**Check again** queues rechecking of the same job on Resume).
  - Success requires a verified MP3 in the output folder.
  - A failed local copy is retried without regenerating speech.
  - Pause, Stop now, Resume and New batch all work in the same window.
- **Rehearse (no cost)** checks each file's real admission and exact maximum, a writable output folder, names, audio tools and the approval.
- **Redacted diagnostics:** events 1640–1650 retain safe IDs, counts, timings, fixed reason codes, failure category/numeric error, queue recovery and user-action stages, plus amount currency/scale. Batch events persist in trend history; each saved job is linked by IDs, and a logging failure cannot interrupt narration. The rolling ledger counts other batches' unstarted approved files as well as their holds.
- **Accessible complete safe support export:** a separate Diagnostics action saves a safe-history ZIP locally with contents/exclusions, visible destination, cancellation and duplicate-click protection. The existing metadata-only JSON action is unchanged. Stream all captured routine records and safe permanent trend history without aggregate byte, record-count or recognized-file limits. Unsafe, malformed or oversized individual records receive explicit omission markers; private captures remain excluded. Failed or cancelled exports do not publish partial archives.
- **Independent handoff review:** renewing approval replaces only unstarted requests, preserves committed jobs, and counts completed files against batch and rolling limits. Retained jobs are not counted twice against Long Audio headroom. New LINEAR16 approvals include one extra cent for rounding the two children of one split; further recovery remains bounded by the approved family maximum. Malformed approvals are isolated so they cannot hide unrelated committed batch jobs.
- **Clearer recovery guidance:** Check again marks a saved job for rechecking on Resume; it cannot poll Google without a saved operation ID. Completed replacement audio remains visibly separate from any earlier unknown outcome and retained safety hold.
- See [docs/BATCH_NARRATION.md](docs/BATCH_NARRATION.md).

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
