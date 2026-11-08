# Batch MP3 from TXT files

Make one MP3 for each of several TXT files with one voice, one review and one approval. Source files are only read. Each file becomes its own library document (exactly like **Import…**) and its own MP3, in the order you set. Files are never merged, and no extra tracks are created.

## Using it

1. Open **Create MP3** and select a verified voice. The whole batch uses it.
2. Choose **Batch MP3 from TXT files…** near the top. It is also under **Create MP3** in the side panel when connected.
3. Choose **Add TXT files…** (multi-select) or **Add a folder…** (top-level `.txt` files; subfolders are not included).
   - The first files are listed in natural order: `2` before `10`, full-width digits included.
   - Files added later go **after** the current list, so an order you arranged is kept.
   - **Move up**, **Move down** and **Remove** change the list.
   - A file with the same text as an earlier one is **kept** as its own track and marked "same text as file NN".
   - Only files that cannot be used are left out, with the reason on the row: empty, not UTF-8/UTF-16 (save it as UTF-8 and add it again), over 32 MB, unreadable, or not `.txt`.
4. Choose the engine:
   - **Long Audio first** (recommended): Long Audio for every file it can take, the ordinary engine for the rest.
   - **Ordinary engine only**.
   - **Long Audio only**.

   Also choose what happens when a file fails: skip it and continue (default), or stop the batch.
5. Optionally tick **Number the MP3 files** (01, 02, …). A title that already starts with a number, such as `01 Preface`, is never numbered twice.
6. Choose an output folder; it defaults to the source folder. An existing file is never overwritten. A clashing name gets ` (2)`, ` (3)` and so on, and the window says which names changed. Names are checked again when you save and when you start.
7. Choose **Add to library and save batch**. The batch is saved in the profile (`batches\<id>.json`) and survives closing the app or a crash. Reopen it from **Open a saved batch**.
8. The window then reviews every file, without sending anything.
   - Each row shows its engine and the most it may cost.
   - The review summary shows the total and how much room is left in the installed Long Audio cap.
   - It also lists any files that still need an ordinary-engine admission, and any that no allowed engine can take.
9. Approve:
   - **Approve N files, up to …** (in-app mode).
   - **Export batch request for review…** (reviewed mode; see below).
10. If the batch has Long Audio files, tick the Long Audio consent to save private provider responses (it is required by Long Audio, and worded exactly as in the single-document Long Audio window).
11. **Rehearse (no cost)** checks every file's real admission and exact maximum in its engine. It also checks:
    - that the output folder is writable (by creating and removing a small probe file of its own);
    - the names and the audio tools;
    - the approval.
12. Choose **Start**. Files run one at a time.
    - **Pause after this file** stops before the next file starts.
    - **Stop now** stops at the next safe point. A request Google already received may still finish and be charged. Its saved job is preserved, and an unknown request is never automatically resent.
    - **Resume** continues the saved job after either, subject to its existing approval and reconciliation checks.
    - When the batch finishes, an ordered playlist (`<name> playlist.m3u8`) and a CSV summary are written to the output folder, never over an existing file.

Amounts are upper limits, estimates and holds, shown in Singapore dollars with the billing currency beside them. They are never a Google bill.

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
- **Redacted logs.** Events 1640–1650 retain IDs linking batches, items and jobs, counts, timings, fixed reason codes, safe failure categories/numeric error codes, queue recovery observations, user-action stages, and amounts with currency and decimal scale. They contain no file names, source text, paths, credentials or exception messages. These batch events persist in safe trend history; diagnostic observer failure cannot change narration or cause a retry.

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

**2. The per-batch approval** binds the exact source hashes, documents and revisions, each file's engine, its exact plan or Long Audio request binding (which covers the saved text, attempt and output object), its maximum, the total and an expiry. Any change before a file starts needs a new approval. Approvals are stored side by side and never overwritten, so a file that started under an earlier approval always keeps it.
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
- **Subfolders** are not scanned.
