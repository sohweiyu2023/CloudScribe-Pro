# Accepted-output continuation

This source supports an explicit, independently reviewed continuation after a
previous run ends. It does not renew a run, supply Google credentials, approve
spending or bypass a platform-required user handoff.

## Boundaries

- Keep the original schema2 manifest and canonical proposal hashes unchanged.
  Schema3 adds an accepted-evidence projection and the exact monetary snapshot;
  remaining permissions are the original members minus verified accepted members.
- The pilot must already be accepted. No new pilot permission is created. An
  accepted member, duplicate authorization, missing output journal, changed media
  or uncertain submission cannot become a new request.
- A distinct schema2 handoff with `TransitionKind=accepted-continuation` links the
  old schema2 register to an inactive schema3 register. A new human approval and
  one new fixed six-hour window are prerequisites. Old events, holds, counters
  and deadlines remain intact. Both ancestor retirement links are checked.
- Actual closed-database, queue, receipt, journal, source, media and financial
  evidence is rechecked by the bridge before installer effects. Caller-provided
  accepted booleans or success receipts do not establish evidence or authority.
- Initial admission requires the remaining members to be untouched. Retaining an
  already admitted policy and revalidating one clean held request are separate
  modes; they never authorize an accepted replay or an additional reservation.
- The existing production article policy, financial guards, request compilation,
  provider adapter, local output persistence and complete-article export remain
  in use. This is not a separate synthesis implementation.
- Limits remain six hours, 24 catalog/bootstrap refreshes and 24 total admissions.
  Expiry, failed or uncertain work, mismatched identities, replay and concurrent
  claims fail closed. There is no automatic paid retry or silent extension.
- An explicitly reviewed continuation can retire a wall-expired scope2 despite
  a lower monotonic reading. It records the raw observation separately from
  nondecreasing journal order and does not assert a reboot. Old anchors and
  ordinary rollback guards remain unchanged. Backward UTC, pending work, failed
  stops or a second reset during retirement still reject the transition.

## All-in budget evidence

Let P be retained project commitments, N the maximum new pretax reservations,
F an independently reviewed upper bound for all applicable project tax/fees,
and C the approved all-in ceiling. Admission requires P + N + F <= C. The bound
must account for tax/fees on earlier commitments as well as new work. A planning
contingency is not a verified tax rate, invoice amount or retry allowance.

The continuation projection requires a nonnegative all-project tax/fee upper
bound and a separately reviewed evidence digest. Its pretax ceiling plus that
bound must be at most the approved overall ceiling. Missing or excessive bounds
are rejected.
The validator checks arithmetic and identity pins; a hash does not authenticate
billing facts. Before any live admission, an independent reviewer must validate
the evidence against the actual billing context and pin the exact manifest and
policy. If the bound cannot be established, live generation stays blocked. Do
not manufacture evidence or assume a tax rate to make the calculation pass.

## Offline verification

From the repository root:

```powershell
python -m unittest discover -s tools/completion_workflow -p 'test_*.py' -v
pwsh -NoProfile -File scripts/Test-CompletionWorkflowGate-SelfTests.ps1 -PythonPath <reviewed-python.exe>
```

Fixtures create fresh temporary synthetic profiles, inert executable/media
bytes and mocked provider metadata. They never select the user's profile,
credentials or accepted audio. An offline pass is not native acceptance, CI,
installation, live billing verification or permission to execute.

## Operational handoff

An expired local review or old manual Reserve instruction is not reusable.
Prepare fresh reviewed continuation bytes only after the above gates and a
new exact approval. Start the window after installation/readiness is verified.
If platform review requires the user to reserve manually, the user must perform
that exact current action; a chat approval alone does not replace the handoff.
After any uncertain action, reconcile actual rows before considering another.
Keep private manifests, billing evidence, transcripts, audio and profiles out
of public source and support exports.

## Conditional renewal template

After release verification and an independently reviewed all-project tax/fee
bound, authorize a fresh fixed six-hour window for the exact untouched remainder,
using the unchanged approved text and voice. Preserve all accepted ranges and
historical commitments. Pin the explicit maximum new reservation count, pretax
amount, allocation ceiling and overall ceiling to that exact reviewed remainder.
No live account, financial snapshot or approval record belongs in this document.

Permit at most 24 catalog/bootstrap refresh operations and 24 total reviewed
admissions, including zero-cost revalidation of the same clean held request.
Any text-free capability checks remain within that refresh count. Capture stays
off. Stop on the first failure, uncertainty, mismatch, limit or deadline, with
no automatic paid retry and no silent extension. Reuse every accepted output;
assemble exactly two continuous MP3s and verify ordered complete coverage, full
decoding and audible starts, ends and joins.

This wording is a proposal, not an active policy or an instruction to click
Reserve. If the platform requires manual reservation, hand the user the exact
freshly reviewed action only after valid admission and receipt reconciliation.
Never refer the user to the expired reservation instruction.
