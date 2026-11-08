# Audio workflow usability review

## Changes in this candidate

- Remember an explicitly approved FFmpeg/ffprobe pair per profile. Revalidate both binaries before reuse, ask again after changes or removal, and expose Change audio tools. Prefer actual Chocolatey binaries rather than wrappers.
- Show verified text coverage separately from provider processing. The provider does not expose a reliable processing percentage or completion-time estimate.
- Show elapsed generation time and recorded active run time separately. Pauses count toward elapsed time; provider waits count toward active run time. Older or interrupted timing is labelled incomplete rather than reconstructed as fact.
- Present per-job retained commitments and attempt counts separately from cumulative provision and the approved cap. Failed, unknown and replacement attempts remain counted. Actual provider billing is not available from these records.
- Show output location and size, use title-based Windows-safe names, and retain playback, save-copy and open-folder actions behind existing integrity checks.
- Keep cancellation explicit, prevent dismissal during output selection, preserve prior selection when reselection is cancelled, and provide actionable export errors with diagnostic references.
- Remove inactive resume/stop controls from completed Long Audio jobs.

## Review boundaries

The generation authorization chain, financial caps, unknown-outcome reconciliation, saved audio and content validation remain unchanged. No new article-length or retry limit is introduced. File-schema bounds and native filename limits protect storage; they do not truncate article content.

Timing and diagnostic summaries are observability only, never authority to resend, spend, release a hold or mark audio complete. Private article text, credentials and raw provider responses are excluded from ordinary summary records.

## Required acceptance checks

- First tool choice, same-profile restart, changed encoder, changed probe, removed tool, corrupt preference, alternate profile, selection cancellation and explicit change.
- Fresh generation, resumed job, stopped job, interrupted timing, older job without timing, provider waiting, assembly and final verification.
- Success, export cancellation, unavailable output, unwritable destination, altered output and repeated clicks.
- Small window and high display scaling, keyboard access, clear completed state and output discovery.
- Synthetic tests and strict solution checks first. Native tests use isolated fixtures; no paid provider requests are implied.

## Remaining distinctions

The new durable timing and spending presentation targets the Long Audio workflow. Standard whole-article synthesis and single samples retain their separate authorization and progress models; do not describe their timing as covered until separately verified. Historical active generation time and actual billed charges cannot be recovered from an uninstrumented job or conservative hold ledger.

## Follow-up review fixes (7 November 2026)

- Bounded waits: operation-status GETs are replaced after two minutes without an answer (event 1625); one private download-and-verify pass is bounded to 30 minutes; TCP keepalive helps detect dead peers but is not an end-to-end response deadline. POSTs still have no application deadline, so an accepted operation is never lost to a local timer.
- **Stop waiting now** ends a wait immediately (event 1626) after **Stop after current action** has been requested; the dialog explains why it cannot close while a run is active.
- The final MP3 gets a fixed 1.5 dB headroom (the measured Anatta MP3 decoded at +0.35 dBFS with 620 over-range samples; with headroom it peaks at about -1.1 dBFS) and an ID3 title tag. Duplicate WAV snapshot copies are removed after a verified receipt.
- FFmpeg re-prompts now say whether the approved tools changed, disappeared or the setting was unreadable.
- The run summary separates the list-price estimate for the article from safety holds and counts reserved attempts rather than proven network submissions.
- Dialogs and the guided walkthrough share the main window's violet and gold theme; Long Audio shows a plain-language headline, a five-step tracker and a raised action bar.
