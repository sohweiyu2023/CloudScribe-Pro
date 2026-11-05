# Delivery and observability lessons

These lessons apply to CloudScribe and should be checked in other projects before their next release. They record failures and required acceptance criteria; they do not claim all listed work is complete.

## Test the visible user journey

- Passing a build, backend tests or a simulated provider test does not establish that a person can finish the task in the installed app.
- Trace the real entry point through input, connection, voice selection, cost review, approval, start, progress, failure recovery, complete output, playback and export.
- Keep the primary action visible at the bottom of the dialog. Do not put disabled playback controls ahead of the action that creates the audio.
- A cost review is useful even when paid authorization is absent. Separate read-only planning from executable spending approval and explain their difference.
- Every disabled action needs its actual reason and a concrete next step. Do not send users through a review action that only changes an unrelated status line.
- Check all actual dynamic dialogs at the available screen size. Record current system scaling and distinguish simulated viewports from real monitor scaling. Never change Windows display settings to perform these checks.
- Cancelled pickers and invalid local paths must remain correctable before any job start. An uncertain job start must remain blocked until reconciled.
- Show checking, generating, saving accepted audio, joining and verifying as distinct phases. State whether cancellation waits for an in-flight outcome.
- A complete-article output needs its own durable receipt and verified playback/export controls. Never represent an assembled article as one short provider response.
- Bind completed-output lookup to the current document revision and plan. Preserve older output without labeling it as the current article.

## Preserve useful evidence safely

- A received HTTP status or canonical code is insufficient when the provider also returned a useful explanation. Historical discarded text cannot be recovered by improving a parser later.
- Do not infer the root cause from a local error hint. Distinguish the original provider evidence, the application's classification and an unverified hypothesis.
- Always retain safe operation, stage, attempt and correlation metadata. Link the article job and internal ordinal to the exact request identity, transport and durable outcome.
- Protected response capture must cover the actual workflow. A one-response exact-request ticket cannot silently cover a later chunk or an entire article.
- Keep sensitive response text in explicitly approved, bounded, redacted, current-user-encrypted local storage. Default it off and exclude it from routine logs and support exports.
- Record observed/read byte counts, actual retained body size, serialized-record size, truncation, omissions and redaction separately. Summary text length is not the provider-body length.
- Preserve partial-response evidence. Do not overwrite its retention result with an empty follow-up observation in an outer catch.
- Distinguish a successful empty private-store read from corruption, failed decryption, access denial and unavailable storage. Never fall back to plaintext.
- Correlate private persistence success to the exact job, ordinal and payload. A previous successful write is not evidence for a later request.
- Diagnostics must not replace the original failure, prevent accepted media from being saved or imply permission to resend it. Missing diagnostics may stop subsequent work while preserving every financial hold.
- Queue overflow, shutdown loss, observer failure and bounded support-export omissions must be visible. A usable logger can still be degraded.

## Keep delivery claims accurate

- Record explicit temporary desktop permissions and shared ownership separately from provider, capture, security and release permissions. Permission to test while a user is away does not expand those other scopes.
- A detached or retired run is a distinct lifecycle state. Test the actual transition from that state with current installed metadata; a previously valid packet is not necessarily compatible after maintenance release.
- Hash-pinned helper extraction requires every staged source bundle to include the newly pinned dependency beside its caller. Exercise the deployed copy through the real CLI and installer, not just imports from the source checkout.
- Automatic tool discovery must reject network paths and linked ancestors before probing children. A detected executable path is a suggestion, not proof of publisher trust; show its location and obtain an explicit product-level selection before use.

- Save source checkpoints and verified backups before replacing installed files. Reconcile interrupted operations before retrying them.
- Preserve accepted audio and historical commitments; use append-only recovery instead of deleting a failed attempt or relabeling it as unrequested.
- Reuse relevant passing checks. Follow the user's requested local-first iteration; run final GitHub checks on the production-ready candidate rather than making them the development feedback loop.
- Report source, locally built, installed, native-tested, simulated-provider-tested and live-provider-tested states separately.
- Do not call the two requested MP3s complete until ordered text coverage, full decoding and listening checks pass for exactly two continuous files.
- After production acceptance, separately verify main, the installed build, the clean distributable ZIP and fresh downloads from the existing links. A historical HTTP 200 is not a freshness check.
- Record remaining gaps and external limits. Do not promise that every provider rejection or every possible logging failure has been anticipated.

## Release checklist for other projects

Apply the same visible-flow, failure-evidence, privacy, interruption and delivery distinctions to each owned project. Reuse existing logging and recovery infrastructure. Do not copy credentials, user documents, audio, profiles or private diagnostics into public source or release packages. Each project's owner must verify its own installation and distribution paths before claiming completion.

## Writer boundaries and realistic timing

- Test the exact serializer output at a language boundary. A native UTC timestamp may use `+00:00` while register timestamps require `Z`; compare aware instants at the boundary without weakening the register grammar.
- A maximum read allowance is not an appropriate allocation size for every file. Read bounded chunks through EOF or the limit-plus-one sentinel; preserve link checks, exact returned bytes and rejection of growth beyond the bound.
- Measure the real installation inventory and retained history. Small synthetic fixtures can pass while production-sized read-only inspection exceeds a subprocess deadline. Fix the measured cost rather than blindly increasing every timeout or caching previous authority.
- Disposing a process handle does not terminate its child. On timeout, stop only the exact owned helper, confirm exit and preserve an uncertain-outcome record. Never infer rollback or permission to retry; a commit may have happened before termination.
- A closed database can have different file bytes with identical logical tables. Reconcile physical identity separately from text, financial, request and media evidence, then bind the exact current closed snapshot. Do not silently overwrite history or skip either class of check.
- Record a prepared clock window separately from activated authority. Expiry still applies after an unsuccessful preparation or inspection; zero spending is not automatic permission to renew the deadline.
- Do not consume a short diagnostic window with installation, review, file pickers and UI discovery. Prepare those steps first, then select the fixed deadline at actual readiness. Context recovery and lost tool output also consume elapsed time; neither extends authority.
- An unused paid reservation is a distinct recovery state. Resume the exact held identity through the existing resolver and a separately approved zero-new-funds mapping; do not create another cent, release the old hold or edit an expired policy.
- Compare the full production writer shape with the reader. Dispatch claims change the queue observation timestamp as well as flags and payload fields. Synthetic fixtures must include that column and ordered mapping/claim timestamps.
- Revalidate a transition both before its first effect and when completing after interruption. Join the predecessor's admitted-policy identity to the exact ledger-held policy, and keep historical policy bytes outside directories that installation replaces.
