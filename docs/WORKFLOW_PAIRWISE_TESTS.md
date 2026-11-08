# Bounded connected-workflow matrix

This is an offline regression lane, not full-product, Cartesian, native Windows, provider, or release certification. It extends the focused catalog-reopen and URL-version tests with interactions between their real persisted and production-composed services.

## Reproducible inventory

`tests/CloudScribe.Architecture.Tests/Fixtures/catalog-workflow-pairwise-v1.json` contains seed **20261108**, ordered axes, the exclusion reason, and every concrete case identity. The SHA-256-tiebroken greedy generator selects **27 cases**, covering **242 feasible value pairs** from **3,744 valid candidates**. **576 candidates** are excluded because explicit URL-only text with v1/v2 omission has no speakable content. Two additional production-store rejection cases verify that constraint rather than silently dropping it.

The seven bounded axes are:

- Actual voices: en-US-Neural2-D and en-US-Chirp3-HD-Aoede.
- Omission: off, persisted url-omission-v1, url-omission-v2.
- Exact text: plain, explicit URL, bare domain, mixed Unicode, explicit URL only.
- Observation: ordinary open, apply filters, explicit refresh.
- Prior evidence: unchanged, missing project/model authorization, expired capability, changed voice metadata.
- Lifetime: same view model, recreated view model with persisted English setup, recreated view model with persisted Chinese setup and stale search cleared.
- Work: pending review, approved but not sent, synthetic accepted outcome, synthetic uncertain outcome.

The generator exhaustively enumerates at most 8,192 candidates, applies the explicit constraint, and greedily covers feasible pairs. A separate verifier enumerates axis-value pairs against the feasible space and searches the checked-in cases without relying on the generator's coverage bookkeeping. The test regenerates and compares the inventory, axes, seed, counts and case order. Negative tests reject impossible constraints, missing pairs despite every value appearing, invalid/duplicate cases, malformed axes and over-budget spaces. The seed orders cases only; disposable database/document IDs are fresh per execution.

## What each connected case actually exercises

The existing `CatalogReopenFixture` supplies disposable SQLite and immutable document blobs, the real catalog bootstrap/loader/binder, fake HTTP that rejects nonempty synthesis payloads, and an in-memory synthetic credential vault. No real account, profile, vault, provider, installed application or foreground UI is accessed.

`MatrixPipeline` constructs the actual Studio capture, queue-state resolver, request-bound authorization owner, independent production evidence resolver, intent assembly coordinator, production compiler/pending publisher, explicit spend approval service, durable spend store and runtime request source. It uses the same request-invalidation wiring as production composition. A real bound queue with an in-memory rejecting transport canary lets tests invoke Generate after authority is invalidated and assert zero canary calls. A separate positive-control case proves this canary is reachable exactly once after explicit synthetic approval and does not retry. No real provider transport is created and output/playback callbacks fail if reached. The lane does not fabricate production evidence or publish a read-only recovery preview into the ordinary approval path.

Each case asserts:

1. Off/v1/v2 text matches independent literal expectations, including deliberate v1/v2 bare-domain differences. Persisted copy replay returns the same document/revision and exact text.
2. The compiled provider JSON, voice, account, project, payload digest, visible review and computed estimate bind to that exact text. Unconfirmed approval is rejected without durable authority.
3. An explicit synthetic confirmation uses the real approval chain only where the work-state case requires it. Completion/uncertainty are deliberately seeded through real durable queue claim/outcome operations. They are not physical provider submissions or verified audio completions.
4. Ordinary open/filter retain unchanged evidence and its original expiry; explicit refresh or missing/expired/changed evidence creates a new identity. Text/payload/estimate remain exact while old approval cannot authorize a changed envelope.
5. Reopening/observing does not revive transient review or executable authority. Same-identity completed/uncertain queues reject a new ordinary preparation, refuse a second claim, and cannot be erased by stale initialization. Changed identities require fresh review and remain unapproved.
6. Original source revision and immutable bytes, previous durable queue identity/outcome and previous durable authorization remain unchanged. Synthetic vault writes do not increase, paid transport count stays zero, and generation remains unavailable.

Four additional source-edit cases cross both voices and both omission versions with unsubmitted/committed work. They save a genuine new original revision after review, assert the stale narration copy cannot be approved/reprepared, preserve original blob bytes and edited source, and keep the exact committed uncertain identity for later reconciliation without a second claim.

## Scope and exclusions

- C01/C02/C13, C14/Q09/Q11, D02/D10/D11 and B01/B02 gain composed interaction evidence for the bounded axes above. This does not replace the existing real additive-admission installation/review tests or claim a grant-install operation in every pairwise case.
- Neural2 and Aoede are supported ordinary synchronous text-compilation paths here. Standard/Studio, short samples, long audio, batch, sentence/refined recovery, SSML limits and alternate output encodings remain in their dedicated suites. Their support rules are not invented as extra matrix values.
- `completed` means a seeded confirmed accepted queue outcome, not decoded/assembled/playable audio. Output/assembly failures and physical dispatch are covered elsewhere.
- Recreated models and stores over real disposable persisted state model restart boundaries. No process crash, actual native window close, focus/keyboard behavior, delayed response race or installed-profile lifecycle is certified. The separate cancellation/UI regression lane owns those cases.
- Missing authority means a deleted project/model authorization in the disposable database; it is not an exhaustive sweep of every admission/capability/pricing mutation.
- Shared two-voice all-in caps, actual additive grant acceptance, live-account spend reconciliation, listening, assistive-technology speech, DPI/contrast and installed shortcut acceptance remain separate gates.
- Pairwise coverage proves only that every feasible two-axis interaction in this documented bounded model is represented by an actually executed connected case. It does not prove all higher-order sequences or all 74 scenario families.

## Execution

Use the repository's pinned SDK and locked restore. Run strict Release build and format verification, then the Architecture project with a filter selecting `WorkflowMatrix*` plus `ConstrainedPairwiseTests`. Keep detailed output/TRX outside the source checkout. The full compiled/governance aggregate belongs on the final combined candidate, not on each independent worker branch. Checked-in case inventory is a reproducibility artifact; a test report must separately record actual execution status and source commit.
