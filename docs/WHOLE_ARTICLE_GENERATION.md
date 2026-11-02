# Complete-article MP3 workflow

The complete-article action reviews the original saved article, exact voice, retained commitments, maximum new cost and expiry once. Its durable job processes admitted internal requests in order, reuses verified accepted audio and calls the existing complete-coverage MP3 exporter. Users do not select or approve each internal request.

## Separate authority boundary

This workflow requires an independently reviewed schema-3 article policy with an explicit `WholeArticleGeneration` grant naming exact plan hashes. Existing schema-1/2 request policies cannot authorize it. The ordinary request approval API also cannot substitute for whole-article consent under schema 3. No implementation loop fabricates per-request user confirmations.

The installer still verifies build, profile, account/project, source-approval, immutable plans, historical financial pins and budget ceilings. A human's whole-article confirmation binds a short-lived exact review and aggregate maximum. Internal reservations use that durable consent together with current compilation and financial evidence. The existing durable queue claim remains the only provider dispatch boundary.

This is a product workflow, not an alternative route around a platform-denied financial action. Live use requires independent admission of this workflow and any mandatory action-time platform handoff. Existing denied request actions remain denied.

## Durability and limits

- Consent and each state transition are recorded transactionally in the existing profile database, with append-only history and one job per policy/plan. A profile-wide worker lock serializes execution.
- Every request uses the production compiler, authorization reader, queue coordinator and accepted-output journal. Identity or budget mismatch, cancellation, failure, uncertainty and expiry stop the job. No automatic paid retry, hold release, accepted-audio regeneration or silent renewal occurs.
- Restart reconciles a saved accepted receipt before advancing. An interrupted active request without a verified saved receipt stops for reconciliation. An interrupted assembly stops for local output inspection. These cases are not inferred safe from elapsed time.
- Catalog/bootstrap refreshes happen only between completed requests and only within the independently admitted 0–24 refresh allowance. A refresh is counted durably before starting. The existing completion gate still applies independently where installed. Refresh can include an empty capability probe; failure stops, and no fallback voice is selected. It cannot refresh an already reserved request or renew paid authority.
- Output uses the existing complete ordered-coverage check, FFmpeg assembly, duration check and full decode. Its path, byte length and hash are retained privately; completed-output readback checks the file again. Export never overwrites an existing destination.
- Routine records contain identifiers, hashes, counters and fixed reason codes, not raw provider explanations or article content. Sensitive capture is never enabled by this workflow.

## Verification and release qualification

Tests use synthetic profiles, fake provider responses and fake media adapters. They verify transactional consent, old-policy rejection, refined-plan lineage, accepted reuse, budgets, expiry/rollback, duplicate prevention, stopped-job dispatch rejection, interruption, catalog limits, append-only history and output readback. Production-adapter tests exercise the existing compiler, spend reader, durable queue and exporter with those fakes; a simulated decode is not a native decoder acceptance result.

This source change does not install or admit a live policy. Exact-head CI, independent review and isolated native acceptance must pass before release. Current narration policies, reservations and accepted audio are not migrated or used by offline tests. An engineer must reconcile any stopped/uncertain request before separately reviewed recovery; the UI does not promise automatic recovery from every external failure.
