# Private provider validation diagnostics

Normal logs retain HTTP status, response-read metadata and request hashes without provider-message text. Older messages discarded by those logs cannot be recovered retrospectively. A generic error category does not mean Google supplied no explanation.

For an unfamiliar Google HTTP400/INVALID_ARGUMENT explanation, open **Workspace inspector → Advanced tools → Private provider error diagnostics**. This feature is off by default.

1. Save the document and select its current Google account and voice.
2. Read the privacy notice, check the explicit consent box and choose **Enable once for selected saved request**.
3. This arms diagnostics for that exact saved document, revision, account, voice and compiled request for at most ten minutes. It does not approve spending or send anything. Any generation still needs its ordinary separate authorization.
4. The first matching authorized dispatch consumes the opt-in even on success, cancellation or failure. Changing the selection/text, disabling capture or closing the app cancels pending opt-in. It is never restored automatically after restart.
5. After an independently authorized request, reopen the private view and choose **Read private local records**. The read-only text cannot execute links, markup, approvals or retries. Check whether saving succeeded; a pending or failed private write does not establish retention.

Only the bounded parsed validation message is eligible. Missing, ambiguous, invalid, oversized or other-category responses retain fixed status metadata instead. Unknown wording is preserved after redaction; it need not match a known-message classifier. HTTP outcomes, reservations and reconciliation rules are unchanged.

Known access-token forms, credential-shaped values and full-input echoes are removed before truncation. Known contiguous input fragments of at least eight Unicode scalars, Unicode-normalized/newline-normalized forms and supported JSON/URL/base64 forms are also suppressed. This cannot guarantee anonymity for arbitrary transformed, very short or unrelated personal data. Remaining text is always **PRIVATE**.

At most eight records are retained, each at most4KiB including JSON and at most2KiB UTF8 message text. Records expire after24hours and are removed on the next store access; interrupted temporary records are cleaned on access. The view marks truncation. A single bounded persistence worker receives already-redacted records only, with no credential/request context. Slow or failing storage never delays the known provider outcome. Orderly disposal allows at most250ms for pending persistence; immediate process exit or failed storage can still lose a private record. Readback is required to verify retention.

The private store is a sibling of the exact configured profile, under `.cloudscribe-private-diagnostics/<profile-hash>`. An isolated QA profile keeps its private store in that same isolated parent; there is no fallback to the real user profile. This location is outside the existing profile/Generated backup roots and normal support-export roots. Normal diagnostics exports, telemetry, Git/source packages and sharing packages must never include these private records. An explicit external copy or OS/user backup may outlive the active-store retention period; old backups are not automatically purged.

This setting does not clear failed-attempt holds, change budgets, authorize a paid test or diagnose messages that were never retained. Keep failed reservations until actual billing is reconciled, and stop after any specifically authorized bounded test.
