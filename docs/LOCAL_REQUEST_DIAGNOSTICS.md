# Local review, reservation and capture failures

Local operations report a fixed, privacy-safe reason instead of discarding every failure behind a generic message. This does not submit audio, enable private capture, authorize a retry, renew admission or alter financial holds.

The local **request review lasts ten minutes**, separately from the independently reviewed admission's expiry. An admission can remain current while its displayed request review has expired. Prepare a fresh local review and inspect any saved reservation before continuing; never clear an earlier hold or repeat uncertain dispatch to refresh presentation. The ten-minute limit and transactional reservation behavior are unchanged.

## Event 6300, schema 1

The existing bounded local diagnostic sink records an independently generated operation correlation, fixed stage and reason, review age in milliseconds, local-review expiry, admission expiry and an evidence-completeness flag. The normal logger already supplies application version, session and timestamp. Unknown review age is explicitly `-1`; absent expiry remains absent. A reason is never inferred from exception wording.

No exception object, exception message, article text, account/credential identifier, file path, raw provider response or private diagnostic record is passed to this event. Unknown exceptions remain visible as `Unexpected`, and unclassified guards as `ValidationRejected`, with incomplete evidence rather than an invented cause. Wrapped first-party reason metadata is read within a bounded chain. Logging or exception-metadata failures cannot replace the original operation failure; advisory logger failures use the existing process-local diagnostic-loss counter.

| Operation | Retained evidence | User recovery |
| --- | --- | --- |
| Article open / coverage read | Stage, safe error family; missing detail explicitly incomplete | Check saved source and current admission |
| Request review | Stage, classified local reason where available | Verify selection before fresh review |
| Reservation | Missing/expired review, confirmation/selection/compilation/admission/financial changes; distinct expiry metadata | Inspect saved reservation first; prepare a fresh review if appropriate |
| Local output repair / export | Stage, safe storage/access/validation category | Reuse accepted outputs; no regeneration |
| Private capture open / read | Stage, safe storage/access/validation category | Inspect local availability; no generation-state change |
| Private capture arm | Unsaved source, changed selection/identity/consent, cancellation or unexpected failure | Capture is disarmed on failed/cancelled opt-in; verify exact saved request before intentionally enabling again |
| Historical recovery open / review / reservation | Stage and classified reason; unavailable timing remains unknown | Inspect retained attempts and holds; no automatic retry |

Failed article review/reservation UI state is discarded in memory and its acknowledgement cleared. Durable reservations and financial history are not deleted. An unexpected failure after a durable commit is not described as proof that nothing was reserved: inspect the queue before another action.

Private one-response provider diagnostics retain their separate explicit consent, exact-request binding, ten-minute one-response window, local retention and normal export exclusion. This change does not enable that mode. See [private diagnostics](PRIVATE_PROVIDER_DIAGNOSTICS.md) and [Google failure diagnostics](GOOGLE_FAILURE_DIAGNOSTICS.md).

Offline tests cover the exact ten-minute boundary, expired preparation/service reviews without reservation, unchanged fresh-review behavior, unknown and wrapped exceptions, hostile metadata, sensitive-message exclusion and throwing loggers. They do not establish a historical runtime cause when an older build discarded the original exception, nor prove a provider request succeeds. No live provider request is needed to test these diagnostics.
