# Tracked follow-up fixes

## CS-CATALOG-CANCEL: allow safe cancellation of comparison catalog loading

Status: **open**. Scheduled after the sample-critical 1.0.6 release and its exact approved comparison work. This is a confirmed pre-existing UX defect, not a completed acceptance check or a regression introduced by 1.0.6.

### Reproduction and impact

In the Voices comparison window, begin **Load connected voices / apply locale** or **Refresh connected voices (full verification)** with a delayed catalog dependency. While the operation is busy, Close is disabled and the window Closing handler vetoes dismissal. The generation Stop waiting control does not cancel this operation. The local 30-second timer only requests cooperative cancellation; it is not a guaranteed wall-clock return. The user cannot dismiss a slow lookup immediately.

Source: `GoogleTtsUsabilityHotfixBinder.VoiceComparison.cs`, the shared busy wrapper, catalog Load/Refresh handlers, UpdateControls and window Closing handler; `GoogleTtsUsabilityHotfixBinder.VoiceComparison.Presentation.cs`, generation-only Stop waiting binding. Behavior is also present in baseline `68d5b2b9287f52b0696617fd10349dd23140bf98`.

### Contained fix scope

Add catalog-specific cancellation and an explicit pending-close state. Request cancellation, wait for that operation to unwind, then restore the appropriate Studio state and close. Do not simply remove the shared busy guard. Late completions must not overwrite a restored/newer selection or update a closed dialog. Preserve explicit-refresh versus retained-loading semantics, diagnostic single-use claims, original expiry, spending consent, active generation/export safeguards and uncertain-outcome handling. Do not add a production bypass or fake-provider route.

### Required regressions and acceptance

- Delayed ordinary load and explicit refresh: Close, Escape and normal window close request cancellation, unwind safely and restore focus/state exactly once.
- Close/reopen and newer navigation win over late success, cancellation and failure; repeat clicks cannot resurrect or duplicate work.
- Diagnostic interruption preserves its claim outcome and does not silently renew/reclaim access.
- Generation/export operations retain their separate Stop waiting and durable reconciliation rules; cancellation never implies an uncharged request.
- Assert no synthesis or credential mutation in synthetic cases. Exercise real production controls with a disconnected synthetic host before native acceptance.
- Rebuild, run focused regressions and the final relevant full suite on the exact follow-up commit, then observe the actual Windows close/focus behavior. Do not mark W07 close-during-catalog-load PASS until this fix and its acceptance are complete.

No provider calls, new credentials, spending increase or production data edits are authorized by this tracking entry.
