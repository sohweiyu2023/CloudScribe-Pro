# Tracked follow-up fixes

## CS-CATALOG-CANCEL: allow safe cancellation of comparison catalog loading

Status: **implemented in an isolated follow-up; native acceptance pending**. The frozen sample-critical 1.0.6 candidate and its installed runtime are unchanged. This is a confirmed pre-existing UX defect, not a regression introduced by 1.0.6.

### Reproduction and impact

In the Voices comparison window, begin **Load connected voices / apply locale** or **Refresh connected voices (full verification)** with a delayed catalog dependency. While the operation is busy, Close is disabled and the window Closing handler vetoes dismissal. The generation Stop waiting control does not cancel this operation. The local 30-second timer only requests cooperative cancellation; it is not a guaranteed wall-clock return. The user cannot dismiss a slow lookup immediately.

Source: `GoogleTtsUsabilityHotfixBinder.VoiceComparison.cs`, the shared busy wrapper, catalog Load/Refresh handlers, UpdateControls and window Closing handler; `GoogleTtsUsabilityHotfixBinder.VoiceComparison.Presentation.cs`, generation-only Stop waiting binding. Behavior is also present in baseline `68d5b2b9287f52b0696617fd10349dd23140bf98`.

### Implemented scope and remaining boundary

Close, Escape and native window close retire the catalog's UI publication revision before requesting cancellation. The comparison window then closes and restores eligible invoker focus without waiting for an uncooperative dependency. Cancellation callbacks run asynchronously; even a throwing callback cannot trap dismissal. Late success, failure and cancellation cannot publish into the closed window or replace a newer saved setup/selection/status.

The underlying operation retains its shared loading guard and diagnostic lease until it really unwinds. Reopening creates a new window, explains the pending operation and disables another catalog load/refresh until that work ends. No diagnostic claim is reclaimed, and cancellation does not imply rollback or an uncharged request. If a dependency never returns, new catalog work stays blocked, but the dialog remains dismissible. There is no claimed wall-clock bound on background cleanup and no automatic retry.

Bootstrap phases recheck cancellation before the next request or persistence step, including after a dependency ignores its token and returns success. Ordinary Load retains coherent evidence and original expiry; explicit Refresh remains a full verification. Sample preparation, generation, replay and export retain their separate busy-close safeguards and exact spending consent. Closed comparison windows detach their generation-control subscriptions. No production fake-provider route or bypass is introduced.

### Required regressions and acceptance

- Delayed ordinary load and explicit refresh: Close, Escape and normal window close dismiss before the held dependency is released; background work unwinds separately and restores eligible focus/state once.
- Close/reopen and newer navigation win over late success, cancellation and failure; repeat clicks cannot resurrect or duplicate work.
- Diagnostic interruption preserves its claim outcome and does not silently renew/reclaim access.
- Generation/export operations retain their separate Stop waiting and durable reconciliation rules; cancellation never implies an uncharged request.
- Assert no synthesis or credential mutation in synthetic cases. Exercise real production controls with a disconnected synthetic host before native acceptance.
- Production-composed headless tests use the real binder controls, real catalog/bootstrap services, synthetic SQLite, a memory-only vault and a deliberately held HTTP handler. The test-only Avalonia.Headless dependency does not run the production application host. Headless state/focus evidence is not Windows focus, screen-reader or DPI acceptance.
- Rebuild, run focused regressions and the final relevant full suite on the combined follow-up commit, then observe the actual Windows close/focus behavior. The full aggregate is held for combined review; do not mark W07 close-during-catalog-load PASS until native acceptance is complete.

No provider calls, new credentials, spending increase or production data edits are authorized by this tracking entry.

## Narration preview dismissal and outline accessibility names

Status: **implemented in a separate reviewable follow-up; native acceptance pending**.

Single-document and batch narration previews now handle Escape through their normal close path and restore the eligible control that opened the preview. The single-document copy-save guard still vetoes dismissal while a save is in progress. Closing a preview neither creates a narration copy nor approves or sends audio.

Both the wide outline panel and narrow outline drawer explicitly bind each generated list item's accessible name to its current paragraph label and paragraph number. This avoids the record's default type/property string without changing navigation offsets or visible labels.

Production controls are checked through the test-only headless host, including repeat preview opening, all three close routes, Escape from the read-only preview text, save-in-progress vetoes, save failure recovery, focus return with eligible versus disabled/detached invokers, and actual automation-peer names after text replacement. These checks do not certify native Windows focus or screen-reader speech. Actual Start Menu shortcut automation remains an operational computer-use limitation, not a source defect; no registry workaround is included.
