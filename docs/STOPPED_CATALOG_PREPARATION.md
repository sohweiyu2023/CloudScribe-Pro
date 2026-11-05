# Catalog preparation after a stopped diagnostic

This maintenance path prepares an existing account without renewing paid authority.
It does not clear the stop, detach the gate, admit a narration policy, reserve money,
enable private response capture, or submit text for synthesis.

## Installation and evidence

Use the existing installer `-ReviewedStoppedMaintenancePlanPath` and exact SHA-256
with a reviewed `expired-reserved-diagnostic-preparation` transition. The operator
requires an activated, expired, stopped schema-6 reserved-request scope, no pending
journal operation, no admitted diagnostic policy and no recorded dispatch. It checks
the closed native ledger, exact unused held request and queue, historical manifest,
accepted media, financial projection, previous gate, source pins and both inventories.
No financial admission marker may be present in either installation.

The prepared installation includes `completion-preparation.json`; its canonical hash
is pinned in the maintenance transition and install marker. The application derives
preparation mode from that marker, while the isolated command bridge independently
requires the matching completed maintenance history and actual ledger evidence.
Changing a marker alone supplies no permission.

## Preparation cycle

The explicitly approved preparation record pins the account-bound scope, old gate,
new build, held authorization and ledger/queue evidence, financial projection,
accepted evidence, independent UTC/boot observation and absolute preparation cutoff.
The trusted UTC interval may be at most five seconds wide, accommodating an honest
whole-second source; the wall-versus-monotonic drift tolerance remains 250 ms and
deadline checks use the conservative upper UTC bound.
Its separate duration is at most 30 minutes. This is a nonfinancial deadline, not a
new paid window. The old paid deadline and clock epoch remain unchanged.

The existing catalog/bootstrap service owns one preparation lease. It permits its
normal bounded cycle of at most two catalog GETs and one input-free `{}` capability
probe, with the existing exact endpoint, project and credential checks. A successful
probe is the expected HTTP 400 `INVALID_ARGUMENT` response with no audio. This is
capability evidence, not a successful audio request.

Claiming the cycle immediately consumes one of the existing cumulative refresh
allowances (maximum 24); cancellation or failure does not refund it. The plan ID,
approval and operation cannot be replayed. An interrupted or expired pending cycle
requires reconciliation; a restarted app cannot claim it again. No automatic retry
or new preparation record is created. Admissions and money counters do not change.
Ordinary status, policy admission, private capture and synthesis remain stopped.

## Recovery limits

The existing file transaction retains displaced installations and supports exact
interruption readback. Rollback is available before consuming preparation. After a
preparation claim, the changed cumulative refresh count intentionally prevents the
old maintenance record from being reused for rollback. A further reviewed recovery
would need the actual new counter state.

The previous reader may reject the new journal event or transition kind. Restoring
its bytes therefore restores a **blocked predecessor**, not a promise of a functional
old application or renewed narration. Never rewrite history or old source pins to
make that reader accept the new history. A later paid run requires a separately
supported, explicitly authorized transition with the then-current counters and
unused-reservation proof.

## Verification boundaries

### Explicit terminal retirement

After a successfully verified preparation cycle, a separate administrative
retirement may close the expired diagnostic session. It preserves the original
stop, paid deadline, consumed counters, financial rows, unused reservation and
accepted media. It does not extend or reactivate the old authorization.

The retirement uses the existing maintenance release transaction: verify cold
native evidence and pinned inventories, claim the terminal record, write the
reviewed ordinary-mode marker, complete the retirement, then archive the required
gate pointer. An interrupted transaction remains fail-closed until reconciled.
Retirement consent cannot reuse preparation consent. A separately pinned reader
may perform this terminal action without changing the immutable installed Python,
source, gate or application pins. It cannot use that exception to install or renew.

An ordinary-mode launch still has no narration admission. Resume of the held
request requires the existing exact zero-new-funds native policy and single-use
mapping; whole-article generation requires its native bounded article grant.
The project-wide cap, historical holds, current pricing, unknown-outcome and
duplicate protections remain mandatory. Retirement supplies none of those grants.

Catalog persistence changes database metadata without changing the retained
financial projection. Retirement therefore pins a fresh closed-database hash and
rechecks the original financial/held/media evidence. It may also pin the updated
hash of one existing `app/logs/cloudscribe-bootstrap-YYYYMMDD.log` file. All other
installation entries remain identical to the frozen inventory; this exception
cannot cover executable, configuration, new-file or removed-file changes.

Synthetic journal tests cover reboot samples, retained paid stops, counters, replay,
canonical IDs, clock/deadline failures and fabricated receipts. A fake-network test
exercises the production catalog service and its preparation command routing. The
operator test uses disposable native-schema ledger/audio fixtures and interrupted
file replacement; the real `-I -S` bridge must report a typed rejection for an
intentionally future fixture clock. No test supplies production credentials or sends
Google requests. Installed/native verification and actual provider behavior must be
reported separately from these tests.
