# Long Audio funding stops and unadmitted journal recovery

## Transport waiting and unknown submissions

A Long Audio POST has no application deadline: an arbitrary local timeout could lose the operation name after Google accepted it. TCP keepalive helps detect an unresponsive peer; detection timing depends on the OS/network, and a detected failure records an unknown outcome under the no-resend rules. An operation-status GET that gets no answer within two minutes is replaced by the next status check; the operation stays pollable and nothing is resubmitted (event 1625). One private download-and-verify pass is bounded to 30 minutes, or the shorter authorized read window; resume repeats only GETs. **Stop after current action** waits for the current step. **Stop waiting now** cancels the wait immediately (event 1626); a request already sent may still finish and be charged, and a cancelled wait is never treated as proof that Google did not receive a POST.

An admitted, send-started POST with no returned operation name remains unknown. Preserve its journal, guard and hold. Do not resend that request, change its record's identifier, withdraw it through the unadmitted-budget recovery, or infer non-submission from an absent output object. A timeout repair does not reconcile an earlier unknown outcome. Further paid work needs verified provider reconciliation or the separately reviewed continuation/regeneration paths below.

## Resume verification cost

The global recovery allowance scan reads every operation's metadata for uncertain outcomes. It does not rehash unrelated completed media at every failed-parent split. Each reused child's normal output path still validates its own identity, checksums and native decoding, and final assembly validates all included parts. Corruption in unrelated audio is detected when that audio is actually reused; this optimization does not admit requests, release holds or skip pending-operation checks.

Resume still walks the deterministic saved tree and performs normal session checks. Removing repeated global media reads reduces disk work; it is not a guarantee against unrelated PC hangs or a claim that all resume overhead is eliminated.

The local provision is a conservative commitment ledger, not a Google invoice. Failed holds, the preserved synchronous grant ceiling and the tax/FX/storage allowance remain counted. A funding stop never resets these records or increases the cap.

New requests receive a non-mutating prospective review before the POST journal is created. Production admission repeats its locked budget and current-readiness checks and persists the exact commitment before transport can send. The final check remains authoritative. A race, cancellation or readiness failure after journaling can still leave evidence requiring explicit reconciliation; preflight does not make every failure safely retryable.

The UI reports the required provision and approved cap, preserves saved audio and disables repeated Generate in that dialog. Event 1621 records those content-free amounts in routine diagnostics, safe export and durable trend history. It does not record article text, credentials or provider prose.

## Existing unadmitted record

`scripts/Reconcile-LongAudioUnadmitted.ps1` is an administrative, explicitly reviewed recovery for a **known pre-admission failure**. It is not automatic cleanup and must not be used merely because a journal looks pristine or a ledger claim is absent.

An independent reviewer must establish the exact pinned build's ordering (durable ledger admission before transport), unchanged ledger provenance and the actual failed action. Seal that evidence's approval digest and the exact install/profile/build/marker/scope/ledger/journal/guard/other-operation/output hashes in a private version-1 plan with reason `VerifiedBudgetRejectionBeforeDurableAdmission`. Never commit that plan, profile, audio or recovery history to source or a public ZIP.

After saving and closing the app normally, `Inspect` requires existing closed-profile, worker, installation and request locks; it creates no lock metadata. It rejects overlapping roots, a mismatched installed profile, any target commitment, observed response, operation name, revision or output, another pending outcome, changed records and malformed or contradictory recovery receipts.

`Archive` first preserves the exact plan, journal, guard and ledger snapshot in private installation history. Checkpointed effects withdraw only the known unadmitted journal/guard from the active directory. No ledger, scope, cap, credential, permission or audio is changed, and no provider call is made. Interrupted archive or rollback can resume only against the same pinned state. `Rollback` restores the exact blocked record and guard; it rejects intervening admissions, scope changes, other-operation changes or output changes. It never rolls back the whole profile or releases money.

For a reviewed live recovery, archive under the pinned predecessor build **before** installing a successor build. Use the existing backed-up installer and its verified build/scope transition afterward. Additional funding, if needed, requires separate explicit authorization and the normal exact-plan update. Reopen the original saved article job; do not create a duplicate or infer authority from the recovery receipt.

## Explicit funding-only update

`Update-LongAudioExcerpt.ps1` version 6 reuses the existing marker-last successor transaction, terminal-operation pins and accepted-audio verification. Its `FundingAdjustment` records the exact previous and newly authorized integer cent caps. Only `TotalGoogleCapUsdCents` and a fresh `SourceApprovalSha256` may change in the two-field form. A four-field form also pins the previous/new `TaxFxProvisionUsdCents` and permits only an increase of that reserve, never a reduction. The reserve must remain below the new cap. This supports retaining an adequate tax/cost margin when increasing a tax-inclusive ceiling. It cannot add or change request descriptors, replacement history, source text, voice, capture terms, parent policy or prior claims. It authorizes no request by itself and creates no new commitment. An interrupted update resumes only from its pinned predecessor and expected old/new bytes; intervening admissions are rejected rather than overwritten.

The unadmitted-recovery packet independently pins launcher EXE and managed DLL identities. Its output inventory includes completed assembly work directories and all their contents; nested changes invalidate recovery or rollback. Private plans and approval evidence remain outside source and public packages.

## Explicit continuation around a preserved unknown range

An unknown POST is never resent, reclassified as failed, or archived as unadmitted. A separately reviewed `DeferredUnknownRanges` descriptor can pin the exact source/root/interval, raw unknown checkpoint, retained claim and legacy observation guard. This narrow migration binds the single owner of the old singleton guard; it does not impose a recording, split, retry or error-history count limit, and it never automatically adds a second unknown outcome.

The native runner reconstructs the unchanged approved recovery tree. It skips only the exact pinned unknown node, verifies saved parts normally and admits genuinely new, strictly disjoint ranges through the existing cumulative ledger. A new or changed uncertain outcome stops continuation. The old journal, claim and guard remain intact. New response guards are request-specific while the global submission/response locks still serialize all network actions. Local orphan audio or object identity for the unknown node requires reconciliation rather than silent omission.

The UI labels the action **Continue other article ranges** and reports a deferred gap. Saved character counts sum verified ranges without counting the missing interval. The final MP3 cannot be assembled or presented as complete until all original source coverage is verified. Another configured root cannot use this root-specific continuation; inspection of an already completed output remains available.

The typed version-2 build transition binds the exact descriptor digest in its plan and native proof. It preserves all existing source/voice/bucket/budget/capture terms and claims, verifies the closed profile and exact unknown evidence, and uses the existing backed-up marker-last install/resume/rollback. Version 1 retains its terminal-only rule. An interruption or rollback preserves the unknown and all audio; rollback restores the previous blocked build, not a fabricated completed request. Public packages contain no private descriptors, plans, profiles, credentials or audio.

`GoogleLongAudioDeferredContinuationInspector` provides a no-provider native evidence check. Its input files should be private immutable review copies; this inspection grants no runtime authority and submits no request. Production activation still requires the exact reviewed installed scope and normal admission.

## Separately approved regeneration despite an unknown outcome

When the user explicitly accepts that both requests might run and be charged, an exact `ExplicitRegeneration` on the deferred descriptor can approve one new attempt for the same unchanged source interval, voice and output format. This is not reconciliation of the original outcome. Its checkpoint, claim and guard remain unchanged and counted. The new record pins a fresh attempt, exact binding, private output object and maximum commitment; no credentials, bucket access or automatic renewal are added.

First introduction rejects an attempt/binding already in the ledger or any corresponding local operation/output evidence. Subsequent build updates preserve the same approval, including after use; they cannot replace or clear it to manufacture another retry. Native provenance uses `long-audio-explicit-unknown-regeneration-v1` for this one same-range edge. Ordinary smaller children still require their own confirmed sentence rejection and strictly shrinking source coverage. No new request can repeat the original attempt. Only that exact regenerated node and its reconstructed descendants may overlap the preserved unknown interval.

The original possible charge, the new commitment and all descendant commitments count toward the same cumulative cap. Another unknown or non-sentence failure stops the run; there is no automatic unchanged regeneration chain. A provider failure may again return no usable explanation, so consent to duplicate risk is not a guarantee of success or an error body.

Final assembly requires complete, contiguous verified coverage and exact retained provenance/amounts. It selects the regenerated audio or its verified descendants once; it cannot combine those with late original output or pretend the original operation is resolved. With no explicit regeneration approval the default remains disjoint continuation with an unfilled gap.

Admission reuses one verified ceiling/proof result only within one scope-validation operation, avoiding duplicate history scans inside that operation. It keeps no authority cache across actions. Both new admission and existing-commitment output authorization repeat fresh scope/proof validation after readiness and compare the structural proof and ceiling; new admission also retains the overlap/pending check, locked ledger/budget, current readiness and unchanged-ledger check before persisting a claim.
