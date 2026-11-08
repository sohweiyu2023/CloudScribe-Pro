# Batch MP3 from TXT files

Make one MP3 for each of several TXT files with one voice, one review and one approval. Source files are only read. Each file becomes its own library document (exactly like **Import…**) and its own MP3, in the order you set. Files are never merged, and no extra tracks are created.

## Using it

The window walks through five steps, shown at the top with a **Next:** line that always says what to do now:
**1 Files → 2 Voice and output → 3 Review and approval → 4 Run → 5 Results**. A button that is not available yet explains why when you point at it (or focus it with the keyboard).

1. Open **Create MP3** and choose **Batch MP3 from TXT files…** near the top.
2. **Files.** Drag TXT files or whole folders from File Explorer into the list, or choose **Add TXT files…** (multi-select) or **Add a folder…** (several folders allowed). Tick **Include subfolders** to look inside subfolders too.
   - The drop area lights up while you drag; anything that is not a `.txt` file is left out and the status line says how many.
   - New files go **after** the current list, so an order you arranged is kept. Among themselves they are sorted naturally by folder and then name (`2` before `10`, full-width digits included).
   - One TXT always makes one MP3. Adding the same file twice is ignored (and reported). Two different files with identical text stay two tracks; the later one is marked "same text as file NN (*name*), kept as its own track", and this note follows the current order after any move or removal.
   - Only files that cannot be used are left out, with the reason on the row: empty, not UTF-8/UTF-16 (save it as UTF-8 and add it again), over 32 MB, unreadable, or not `.txt`.
   - There is no limit on the number of files. Reading and adding show progress and can be cancelled; anything finished before the cancel is kept.
3. **Arrange.** Drag rows by their grip (⠿); a gold line shows where they will land, and the list scrolls when you drag near its top or bottom edge. Select several rows with Shift or Ctrl and drag or move them together; they keep their relative order. Keyboard: **Alt+Up / Alt+Down** moves the selected files, **Alt+Home / Alt+End** moves them to the top or end, **Delete** removes them, **Ctrl+Z** undoes, and **Enter** plays a finished MP3. **Move up**, **Move down**, **Remove** and **Undo** do the same with the mouse. Only waiting files without a saved job can be moved. Files that failed before library import can also be removed and added again; started files remain protected. **Ctrl+Z** stays available after **Delete**, including when you remove the last row.
4. **The draft is kept automatically.** Until you save it, the batch is an *unsaved draft* stored in the profile (`batches\draft.json`: file names, paths, sizes and SHA-256 hashes, never the text). Successfully autosaved drafts return after reopening, including after a crash, and appear as **Unsaved draft** in the batch list. Opening a saved batch or waiting for a voice does not discard the draft. Check the status line for any save error. **Discard draft** clears it, and **Undo discard** brings it back while the window is open.
5. **Voice and output.** Choose the voice in the window (the same verified voices as the main window; **Refresh voices** reads the list again without requesting narration). If Google is not connected yet, keep arranging files; the draft waits. Choose the engine:
   - **Long Audio first** (recommended): Long Audio for every file it can take, the ordinary engine for the rest.
   - **Ordinary engine only**.
   - **Long Audio only**.

   Also choose what happens when a file fails: skip it and continue (default), or stop the batch. Optionally tick **Number the MP3 files** (01, 02, …): the width follows the number of narrated files, and a title that already starts with a number, such as `01 Preface`, is never numbered twice.
6. **Output folder.** It defaults to the folder of the first file. An existing file is never overwritten: a clashing name gets ` (2)`, ` (3)` and so on, and the window says which names changed. Names are checked again when you save and when you start.
7. Choose **Add to library and save batch**. Every file is read again first and must still be there. A file that changed since it was listed is taken as it is now (nothing was reviewed yet) and the window says so; a missing file stops the save and is named. The batch is then saved in the profile (`batches\<id>.json`) and survives closing the app or a crash. Reopen it from the batch list at the top. If adding to the library is cancelled or interrupted, opening the batch again finishes it by reading the files again; a file whose bytes changed is refused rather than narrated with different text. If a file fails before library import, remove it, fix or restore the source if needed, then add it again while additions are allowed. The removed row remains in the saved history; any new file needs a new review and approval.
8. **Editing a saved batch.** Wait until the batch is not running and no other action is in progress. You can add files when the batch is ready or paused, including after earlier files have started; added files go into the library at once and need a new review and approval. Waiting files without a saved job can still be moved or removed while the batch is idle, even after earlier files have started. Moving or removing these files keeps the approval for the remaining unchanged requests. The output folder can change only before the first file starts.
9. The window then reviews every file, without sending anything.
   - Each row shows its engine and the most it may cost.
   - The review summary shows the total and how much room is left in the installed Long Audio cap.
   - It also lists any files that still need an ordinary-engine admission, and any that no allowed engine can take.
   - If the selected voice is not the batch's own voice, choose **Use this batch's voice**.
10. Approve:
    - **Approve N files, up to …** (in-app mode).
    - **Export batch request for review…** (reviewed mode; see below).
    - If this computer has no batch spending setup, the window says so and **Start** stays unavailable. Preparing, saving and rehearsing still cost nothing.
11. If the batch has Long Audio files, tick the Long Audio consent to save private provider responses (it is required by Long Audio, and worded exactly as in the single-document Long Audio window).
12. **Rehearse (no cost)** checks every file's real admission and exact maximum in its engine. It also checks the voice, that the output folder is writable (by creating and removing a small probe file of its own), the names, the audio tools, the spending setup and the approval. The checklist appears under Review and approval; its verdict counts **every** ✗ line, so it never says "ready" while the voice, folder, setup or approval is a problem. Failed and needs-checking files are listed separately, because **Resume** skips them until you choose **Retry** or **Check again**.
13. Choose **Start**. Files run one at a time.
    - **Pause after this file** stops before the next file starts.
    - **Stop now** stops at the next safe point. A request Google already received may still finish and be charged. Its saved job is preserved, and an unknown request is never automatically resent.
    - **Resume** continues the saved job after either, subject to its existing approval and reconciliation checks.
    - Clicking twice, or clicking another action while a step is still working, is refused with a short message; nothing runs twice.
    - When the batch finishes, an ordered playlist (`<name> playlist.m3u8`) and a CSV summary are written to the output folder, never over an existing file.
14. **Results.** The results line keeps done, failed, needs checking, skipped and waiting apart. **Open output folder** opens it; select a finished row and choose **Play selected MP3** (or press Enter). A failed local copy shows "speech is saved": **Retry** copies the verified MP3 again with no new request. **Needs checking** is never resent automatically. A file that once had an uncertain request keeps a note that an earlier request may have been charged, even after replacement audio is verified.

The window works with the keyboard and screen readers (named controls, polite status announcements, focus kept on the moved files), at small window sizes (the button rows wrap and the page scrolls), at high DPI and with file names in any language.

Amounts are shown in three separate places, each labelled: the **estimate** (an upper limit), the **approved allowance**, and what is **held** for started files together with any file whose request may have reached Google. Amounts are in Singapore dollars with the billing currency beside them. None of them is a Google bill; your Google Cloud bill is the only record of actual charges.

## Engines

**Long Audio** sends the whole file as one request and splits it only after a confirmed applicable failure. This is the existing adaptive recovery for sentence-length rejections, available when the installed Long Audio scope uses LINEAR16. The verified result is assembled into one MP3 in the profile. That MP3 is then copied to the output folder, and the copy is verified (size and SHA-256).

A file can use Long Audio when:
- the batch voice and language are the installed Long Audio scope's voice and language;
- its text is at most 1 MB;
- its whole cost, including any splitting, fits the room left in the installed Long Audio cap.

**Ordinary engine** (complete article): the existing sentence-sized workflow, joined into one MP3. A file can use it once an installed article admission covers its exact plan. The review then shows the service's exact maximum; until then the row says "needs an installed article admission", and the file stops with nothing sent.

A file's engine is fixed when it starts and is never switched afterwards, so a charge can never be duplicated by trying another engine.

## Reliability rules

- **One run per batch.** A second window or process cannot start the same batch (an in-process gate plus an OS file lock).
- **Every step is saved before the next.** Saves are atomic (temporary file, flush, replace, keeping a `.bak`), with a revision check. If the main file is ever damaged, the batch loads from the backup and keeps saving. The damaged file is kept beside it as `.damaged-<time>`, never deleted, and a stale copy never overwrites newer progress.
- **The job is recorded before any paid request.** After that, the file is only continued or reconciled through the same job: the article job, or the Long Audio attempt.
- **Unknown outcomes are never resent.** Anything that may hide a submitted request becomes **Needs checking**. **Check again** marks the saved job for rechecking when you choose **Resume**. The engine refuses to continue while reconciliation is required. Without a saved operation ID it cannot ask Google for that request's status; it does not create a replacement request. Completed replacement audio does not resolve an earlier unknown charge or release its retained hold.
- **Success means verified output.** A file is marked done only after its MP3 in the output folder matches the reported size and SHA-256.
- **Saved speech is never regenerated.** If only the local copy failed (folder full, file locked, a name appeared), the file shows "speech is saved". **Retry** copies the verified MP3 again with no new request.
- **Failures stay local.** A failed file never removes or changes finished ones.
- **Redacted logs.** Batch events retain IDs linking batches, items and jobs, counts, timings, fixed reason codes, safe failure categories/numeric error codes, queue recovery observations, user-action stages, and amounts with currency and decimal scale. Source checks also record whether a draft is ready, refreshed or unavailable, or a saved import encounters changed or unavailable bytes, is interrupted or refused, or succeeds after its queue record is saved. They contain no file names, source text, paths, credentials or exception messages. These batch events persist in safe trend history; diagnostic observer failure cannot change narration or cause a retry.

## Support diagnostics and retention

Open **Diagnostics** and choose **Export safe diagnostic history ZIP locally** in the separate safe-history section. Read the contents/exclusions and displayed local destination first. The ZIP is saved in the workspace's `support-bundles` folder; the completed path is shown and nothing is uploaded. **Cancel safe history export** stops an in-progress export, and another export cannot start until it finishes. The existing **Export application metadata locally** action remains separate and creates its metadata-only JSON.

Support ZIPs include regenerated allowlisted routine metadata and permanent safe trend history, including batch events. All records present within each source file's captured byte length are streamed; later appended records belong to a later export. There is no aggregate byte, record-count or recognized-log-file cutoff. `SupportBundleMaximumMiB` remains accepted for configuration compatibility but no longer limits safe exports. `SupportBundleMaximumFileCount` only guards an unexpectedly cluttered directory of non-exportable entries.

This is safe metadata, not an archive of everything. Routine logs still rotate, and private provider captures keep their separate consent and 24-hour logical expiry. Private captures, article text, audio, credentials and databases are excluded from support ZIPs. Malformed, incomplete, unsafe or oversized individual records receive explicit omission markers; schema, record-size and nesting safeguards remain. Export is cancellable, disk errors are reported, and failed or cancelled exports do not publish a partial archive. Original local records are not rewritten by export.

## Spending authority

A batch never creates spending authority on its own. Authority has two layers.

**1. The batch profile** is set once on each computer by whoever administers CloudScribe there. It lives at `<install>\batch-authority\profile.json`, schema 2, and sets:

| Setting | Default |
|---|---|
| `Mode` | `Reviewed` (`Off`, `Reviewed` or `InAppWithinLimits`) |
| `AllowedEngines` | `long-audio`, `complete-article` |
| Per-batch ceiling | about S$25 (`PerBatchCeilingMinorUnits` 1924 US cents at display rate 1.30) |
| Rolling ceiling | about S$100 per 30 days (7693 US cents) |
| Account and project | required |
| Allowed voices | empty list allows any voice |
| Failure policy | skip and continue |
| Approval lifetime | 2 days |
| Profile expiry | required |
| Estimate rate | 3000 (US$30 per million characters, plus a 10% margin) |

The rolling ceiling counts other batches' holds, their unfinished files' approved maxima, and their approved files that have not started yet while those approvals are valid.

These ceilings are limits on what an approval may cover. They are never an approval themselves: nothing runs until you approve a specific batch, with its total shown.

**2. The per-batch approval** binds the exact source hashes, documents and revisions, each file's engine, its exact plan or Long Audio request binding (which covers the saved text, attempt and output object), its maximum, the total and an expiry. Adding a file or changing the approved request content needs a new review and approval. Moving or removing waiting files keeps the existing approval for the remaining unchanged requests. Approvals are stored side by side and never overwritten, so a file that started under an earlier approval always keeps it.
- In `InAppWithinLimits` mode, **Approve** is one click. It is saved only if it fits both ceilings and the Long Audio cap room.
- In `Reviewed` mode, **Export batch request for review…** writes `batch-admission-request-<id>.json` (format v2). The reviewer installs it once with the script below.

**How Long Audio files are admitted.** An approved Long Audio file is added, at run time, to the installed Long Audio scope's list of approved exact requests. The installed scope's voice, language, encoding, project, credential reference, bucket, pricing, total cap, existing jobs and ledger are unchanged and still govern every request:
- the ledger records each commitment against the installed cap;
- a request can never be committed twice;
- the cap still includes everything else the scope approves.

Batch additions are left out unless they fit the cap's remaining room, with room for each file's whole family (the root request plus any splits). They are taken in approval order, so a later approval can never push out an earlier one or an installed job. A family can never commit more than its approved maximum.

An addition whose approval has expired (or whose profile changed) is dropped, unless it already holds a commitment. Committed jobs always stay admitted, so they can still be polled, downloaded and reconciled. If anything about the additions is wrong, they are simply ignored: installed Long Audio jobs never stop working because of a batch.

Ordinary-engine files still need an installed complete-article policy that names their plans, exactly as before.

**Trust boundary.** The app never writes to `<install>\batch-authority\`. `Install-BatchAuthority.ps1` records each installed file's SHA-256 in `pins.json`; the app ignores anything unpinned, edited or bound to another profile folder. In-app approvals live in the user profile and can never pose as reviewed ones. The install folder is per-user, so this is the same process boundary as the existing reviewed policies, not an operating-system permission.

Run the administrative commands below from a reviewed source checkout. The clean Windows package includes this guide, but not the batch-authority installer or a private authority profile; downloading the package does not enable paid batch narration.

```powershell
pwsh -File scripts\Install-BatchAuthority.ps1 -ProfileRootSha256For "$env:LOCALAPPDATA\CloudScribe Pro\Profiles\Studio"
pwsh -File scripts\Install-BatchAuthority.ps1 -ReviewedProfilePath .\batch-profile.json -ReviewedProfileSha256 <sha256> [-ValidateOnly]
pwsh -File scripts\Install-BatchAuthority.ps1 -ReviewedRequestPath .\batch-admission-request-<id>.json -ReviewedRequestSha256 <sha256> [-ValidateOnly]
```

Example profile (`batch-profile.json`):

```json
{ "SchemaVersion": 2, "Mode": "InAppWithinLimits", "ProfileRootSha256": "<from -ProfileRootSha256For>",
  "AccountStableId": "<account>", "ProjectStableId": "<project>", "AllowedVoiceStableIds": [],
  "AllowedEngines": ["long-audio", "complete-article"], "FailurePolicy": "SkipAndContinue", "Currency": "USD", "Scale": 2,
  "PerBatchCeilingMinorUnits": 1924, "RollingCeilingMinorUnits": 7693, "RollingWindowDays": 30, "MaximumFilesPerBatch": 500,
  "ConsentLifetime": "2.00:00:00", "ExpiresUtc": "2027-02-01T00:00:00+00:00", "DisplayCurrency": "SGD", "DisplayRate": 1.30,
  "EstimateMinorUnitsPerMillionCharacters": 3000 }
```

## Not in this version

- **Ordinary-engine files still need an installed article admission.** Admitting them from a batch approval would change the article service's reviewed financial continuity (policy hashes, attempt counts, allocations), so it needs its own independent review.
- **ID3 track numbers** are not written. Order is carried by the playlist, the CSV and the optional `NN` prefixes.
